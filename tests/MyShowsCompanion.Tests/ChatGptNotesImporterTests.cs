using System.Text.Json;
using MyShowsCompanion.Domain;
using MyShowsCompanion.Services;
using MyShowsCompanion.Storage;

namespace MyShowsCompanion.Tests;

public sealed class ChatGptNotesImporterTests : IDisposable
{
    private static readonly Guid SeriesId = Guid.Parse("a0000000-0000-0000-0000-000000000001");
    private static readonly Guid SecondSeriesId = Guid.Parse("a0000000-0000-0000-0000-000000000002");
    private static readonly Guid MovieId = Guid.Parse("a0000000-0000-0000-0000-000000000003");
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly string _databasePath;
    private readonly string _jsonPath;
    private readonly SqliteStore _store;
    private readonly ChatGptNotesImporter _importer;

    public ChatGptNotesImporterTests()
    {
        Directory.CreateDirectory(_dir);
        _databasePath = Path.Combine(_dir, "library.sqlite3");
        _jsonPath = Path.Combine(_dir, "myshows-notes.json");
        _store = new SqliteStore(_databasePath);
        _store.Initialize();
        Seed();
        _importer = new ChatGptNotesImporter(_store);
    }

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ImportsEntityTasteAndMixedNotes()
    {
        WriteDocument(Entity(SeriesId, "  A thoughtful note.\r\nSecond line.  "),
            Taste("Likes unusual protagonists."), Entity(MovieId, "A film note.", mediaType: "movie"));

        var result = _importer.Import(_jsonPath);

        Assert.True(result.Succeeded, string.Join("\n", result.Errors));
        Assert.Equal(2, result.AddedEntityNotes);
        Assert.Equal(1, result.AddedTasteNotes);
        Assert.Equal("A thoughtful note.\nSecond line.", ReadEntityNotes().Single(note => note.MediaId == SeriesId).Text);
        Assert.Equal("Likes unusual protagonists.", Assert.Single(ReadTasteNotes()).Text);
        Assert.Contains(ReadEntityNotes(), note => note.MediaType == MediaType.Series && note.MediaId == SeriesId);
    }

    [Fact]
    public void ReimportAndDuplicateWithDifferentCreatedAtAreSkipped()
    {
        var entity = Entity(SeriesId, "Same note");
        var taste = Taste("Same taste");
        WriteDocument(entity, taste);
        Assert.True(_importer.Import(_jsonPath).Succeeded);

        var exactRepeat = _importer.Import(_jsonPath);
        Assert.True(exactRepeat.Succeeded);
        Assert.Equal(0, exactRepeat.AddedEntityNotes + exactRepeat.AddedTasteNotes);
        Assert.Equal(2, exactRepeat.SkippedDuplicates);

        WriteDocument(Entity(SeriesId, "Same note", "2026-11-11T20:30:00+00:00"),
            Taste("Same taste", "2026-11-11T20:30:00+00:00"));
        var second = _importer.Import(_jsonPath);

        Assert.True(second.Succeeded);
        Assert.Equal(0, second.AddedEntityNotes);
        Assert.Equal(1, second.SkippedDuplicateEntityNotes);
        Assert.Equal(0, second.AddedTasteNotes);
        Assert.Equal(1, second.SkippedDuplicateTasteNotes);
        Assert.Single(ReadEntityNotes());
        Assert.Single(ReadTasteNotes());
    }

    [Fact]
    public void SameTextForDifferentMediaIdIsASeparateNote()
    {
        WriteDocument(Entity(SeriesId, "Same thought"));
        Assert.True(_importer.Import(_jsonPath).Succeeded);
        WriteDocument(Entity(SecondSeriesId, "Same thought"));

        var result = _importer.Import(_jsonPath);

        Assert.True(result.Succeeded);
        Assert.Equal(1, result.AddedEntityNotes);
        Assert.Equal(new[] { SeriesId, SecondSeriesId }, ReadEntityNotes().Select(n => n.MediaId).OrderBy(x => x));
    }

    [Theory]
    [InlineData("unknown-media-type")]
    [InlineData("not-a-guid")]
    public void InvalidEntityReferenceOrTypeRollsBackWholeFile(string badValue)
    {
        _store.AddTasteNote(new(Guid.NewGuid(), "Pre-existing taste", CreatedAt, "chatgpt"));
        WriteDocument(Entity(SeriesId, "Would be valid"),
            Entity(badValue == "unknown-media-type" ? SeriesId.ToString() : badValue, "Bad", mediaType: badValue == "unknown-media-type" ? badValue : "series"),
            Taste("Would also be valid"));

        var result = _importer.Import(_jsonPath);

        Assert.False(result.Succeeded);
        Assert.Empty(ReadEntityNotes());
        Assert.Equal(new[] { "Pre-existing taste" }, ReadTasteNotes().Select(n => n.Text));
    }

    [Fact]
    public void MissingLocalMediaIdRollsBackWholeFile()
    {
        WriteDocument(Entity(SeriesId, "Valid first"), Entity(Guid.NewGuid(), "Missing reference"), Taste("Valid taste"));

        var result = _importer.Import(_jsonPath);

        Assert.False(result.Succeeded);
        Assert.Empty(ReadEntityNotes());
        Assert.Empty(ReadTasteNotes());
    }

    [Theory]
    [InlineData("{not json")]
    [InlineData("{\"schemaVersion\":7,\"entityNotes\":[],\"tasteNotes\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"entityNotes\":[{\"mediaType\":\"series\",\"mediaId\":\"a0000000-0000-0000-0000-000000000001\",\"text\":\"  \",\"createdAt\":\"2026-11-10T20:30:00+00:00\",\"source\":\"chatgpt\"}],\"tasteNotes\":[]}")]
    public void MalformedUnsupportedOrEmptyTextFileDoesNotWrite(string json)
    {
        _store.AddTasteNote(new(Guid.NewGuid(), "Pre-existing", CreatedAt, "chatgpt"));
        File.WriteAllText(_jsonPath, json);

        var result = _importer.Import(_jsonPath);

        Assert.False(result.Succeeded);
        Assert.Empty(ReadEntityNotes());
        Assert.Equal(new[] { "Pre-existing" }, ReadTasteNotes().Select(n => n.Text));
    }

    [Theory]
    [InlineData("2026-11-10T20:30:00")]
    [InlineData("November 10, 2026 +00:00")]
    public void InvalidDateAndSourceAreRejectedWithoutWrites(string invalidDate)
    {
        WriteDocument(Entity(SeriesId, "x", invalidDate), Taste("y", source: " "));

        var result = _importer.Import(_jsonPath);

        Assert.False(result.Succeeded);
        Assert.Equal(2, result.Errors.Count);
        Assert.Empty(ReadEntityNotes());
        Assert.Empty(ReadTasteNotes());
    }

    [Fact]
    public void PreservesExistingNotesAndImportedNotesAppearInVersionTwoExport()
    {
        var oldNote = new AssistantNote(Guid.NewGuid(), MediaType.Series, SeriesId, "Existing note", CreatedAt, "chatgpt");
        var oldTaste = new TasteNote(Guid.NewGuid(), "Existing taste", CreatedAt, "chatgpt");
        _store.AddAssistantNote(oldNote);
        _store.AddTasteNote(oldTaste);
        WriteDocument(Entity(SeriesId, "New note"), Taste("New taste"));

        var result = _importer.Import(_jsonPath);
        var exportBytes = ChatGptLibraryExporter.Serialize(ChatGptLibraryExporter.Read(_store));
        using var export = JsonDocument.Parse(exportBytes);
        var root = export.RootElement;
        var seriesNotes = root.GetProperty("series").EnumerateArray()
            .Single(item => item.GetProperty("id").GetGuid() == SeriesId).GetProperty("notes").EnumerateArray().ToArray();

        Assert.True(result.Succeeded);
        Assert.Equal(2, ReadEntityNotes().Count);
        Assert.Equal(2, ReadTasteNotes().Count);
        Assert.Equal(2, seriesNotes.Length);
        Assert.Contains(seriesNotes, note => note.GetProperty("text").GetString() == "New note");
        Assert.Equal(2, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(2, root.GetProperty("tasteNotes").GetArrayLength());
        Assert.False(root.TryGetProperty("episodes", out _));
        Assert.All(seriesNotes, note => Assert.False(note.TryGetProperty("id", out _)));
    }

    private static readonly DateTimeOffset CreatedAt = DateTimeOffset.Parse("2026-11-10T20:30:00+00:00");

    private void WriteDocument(params string[] items)
    {
        var entity = items.Where(x => x.StartsWith("{\"mediaType\"", StringComparison.Ordinal));
        var taste = items.Where(x => x.StartsWith("{\"text\"", StringComparison.Ordinal));
        File.WriteAllText(_jsonPath, $"{{\"schemaVersion\":1,\"entityNotes\":[{string.Join(',', entity)}],\"tasteNotes\":[{string.Join(',', taste)}]}}");
    }

    private static string Entity(Guid id, string text, string createdAt = "2026-11-10T20:30:00+00:00", string mediaType = "series") =>
        Entity(id.ToString(), text, createdAt, mediaType);

    private static string Entity(string id, string text, string createdAt = "2026-11-10T20:30:00+00:00", string mediaType = "series") =>
        JsonSerializer.Serialize(new { mediaType, mediaId = id, text, createdAt, source = "chatgpt" });

    private static string Taste(string text, string createdAt = "2026-11-10T20:30:00+00:00", string source = "chatgpt") =>
        JsonSerializer.Serialize(new { text, createdAt, source });

    private List<AssistantNote> ReadEntityNotes()
    {
        using var connection = _store.OpenReadOnly();
        return _store.GetAssistantNotes(connection);
    }

    private List<TasteNote> ReadTasteNotes()
    {
        using var connection = _store.OpenReadOnly();
        return _store.GetTasteNotes(connection);
    }

    private void Seed()
    {
        using var connection = _store.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "INSERT INTO Series(Id,Title,Status) VALUES($series,'Series','Смотрю'),($second,'Series duplicate','Смотрю'); INSERT INTO Movie(Id,Title,Status) VALUES($movie,'Movie','Полностью посмотрел');";
        command.Parameters.AddWithValue("$series", SeriesId.ToString());
        command.Parameters.AddWithValue("$second", SecondSeriesId.ToString());
        command.Parameters.AddWithValue("$movie", MovieId.ToString());
        command.ExecuteNonQuery();
    }
}
