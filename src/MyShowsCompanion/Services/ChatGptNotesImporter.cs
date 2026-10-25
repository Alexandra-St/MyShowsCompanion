using System.Globalization;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using MyShowsCompanion.Domain;
using MyShowsCompanion.Storage;

namespace MyShowsCompanion.Services;

public sealed record ChatGptNotesImportResult(
    int AddedEntityNotes,
    int SkippedDuplicateEntityNotes,
    int AddedTasteNotes,
    int SkippedDuplicateTasteNotes,
    IReadOnlyList<string> Errors)
{
    public bool Succeeded => Errors.Count == 0;
    public int SkippedDuplicates => SkippedDuplicateEntityNotes + SkippedDuplicateTasteNotes;

    public string FormatSummary() =>
        $"Добавлено: {AddedEntityNotes} заметок к тайтлам, {AddedTasteNotes} общих. " +
        $"Пропущено повторов: {SkippedDuplicates}.";
}

public sealed class ChatGptNotesImporter(SqliteStore store)
{
    private static readonly string[] DateFormats =
    [
        "yyyy-MM-dd'T'HH:mm:sszzz",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFFzzz",
        "yyyy-MM-dd'T'HH:mm:ss'Z'",
        "yyyy-MM-dd'T'HH:mm:ss.FFFFFFF'Z'"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = false,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    public ChatGptNotesImportResult Import(string path)
    {
        NotesDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<NotesDocument>(File.ReadAllBytes(path), JsonOptions);
        }
        catch (JsonException ex)
        {
            return Failure($"JSON не удалось прочитать: {ex.Message}");
        }
        catch (IOException)
        {
            return Failure("Не удалось прочитать выбранный файл.");
        }
        catch (UnauthorizedAccessException)
        {
            return Failure("Нет доступа к выбранному файлу.");
        }

        if (document is null) return Failure("JSON должен содержать корневой объект.");

        var errors = new List<string>();
        if (document.SchemaVersion != 1)
            errors.Add($"Неподдерживаемая версия schemaVersion: {document.SchemaVersion}. Ожидается 1.");
        if (document.EntityNotes is null) errors.Add("Отсутствует массив entityNotes.");
        if (document.TasteNotes is null) errors.Add("Отсутствует массив tasteNotes.");

        var entityNotes = ValidateEntityNotes(document.EntityNotes ?? [], errors);
        var tasteNotes = ValidateTasteNotes(document.TasteNotes ?? [], errors);
        if (errors.Count > 0) return Failure(errors);

        try
        {
            using var connection = store.Open();
            // Acquire the SQLite write lock before checking duplicates so concurrent imports
            // cannot both observe the same old state and insert the same note.
            using var transaction = connection.BeginTransaction(deferred: false);

            var seriesIds = ReadIds(connection, transaction, "Series");
            var movieIds = ReadIds(connection, transaction, "Movie");
            foreach (var note in entityNotes)
            {
                var exists = note.MediaType == MediaType.Series
                    ? seriesIds.Contains(note.MediaId)
                    : movieIds.Contains(note.MediaId);
                if (!exists)
                    errors.Add($"entityNotes[{note.Index}]: локальный ID {note.MediaId:D} не найден для {note.MediaType.ToString().ToLowerInvariant()}.");
            }
            if (errors.Count > 0) return Failure(errors);

            var entityKeys = ReadEntityKeys(connection, transaction);
            var tasteKeys = ReadTasteKeys(connection, transaction);
            var addedEntity = 0;
            var skippedEntity = 0;
            var addedTaste = 0;
            var skippedTaste = 0;

            foreach (var note in entityNotes)
            {
                var key = new EntityDuplicateKey(note.MediaType, note.MediaId, note.Text, note.Source);
                if (!entityKeys.Add(key))
                {
                    skippedEntity++;
                    continue;
                }

                InsertEntityNote(connection, transaction, note);
                addedEntity++;
            }

            foreach (var note in tasteNotes)
            {
                var key = new TasteDuplicateKey(note.Text, note.Source);
                if (!tasteKeys.Add(key))
                {
                    skippedTaste++;
                    continue;
                }

                InsertTasteNote(connection, transaction, note);
                addedTaste++;
            }

            transaction.Commit();
            return new(addedEntity, skippedEntity, addedTaste, skippedTaste, Array.Empty<string>());
        }
        catch (SqliteException ex)
        {
            return Failure($"SQLite не приняла импорт. Транзакция отменена: {ex.Message}");
        }
        catch (IOException)
        {
            return Failure("Не удалось записать заметки. Транзакция отменена.");
        }
    }

    private static List<ValidatedEntityNote> ValidateEntityNotes(
        IReadOnlyList<EntityNoteInput?> inputs, List<string> errors)
    {
        var result = new List<ValidatedEntityNote>();
        for (var index = 0; index < inputs.Count; index++)
        {
            var item = inputs[index];
            if (item is null)
            {
                errors.Add($"entityNotes[{index}]: запись не может быть null.");
                continue;
            }

            MediaType mediaType;
            if (item.MediaType == "series") mediaType = MediaType.Series;
            else if (item.MediaType == "movie") mediaType = MediaType.Movie;
            else
            {
                errors.Add($"entityNotes[{index}]: неизвестный mediaType. Допустимы series и movie.");
                continue;
            }

            if (!Guid.TryParse(item.MediaId, out var mediaId))
            {
                errors.Add($"entityNotes[{index}]: mediaId должен быть корректным GUID.");
                continue;
            }

            var text = NormalizeText(item.Text ?? string.Empty);
            if (text.Length == 0)
            {
                errors.Add($"entityNotes[{index}]: text не может быть пустым.");
                continue;
            }

            if (!TryParseDate(item.CreatedAt, out var createdAt))
            {
                errors.Add($"entityNotes[{index}]: createdAt должен содержать ISO 8601 дату со смещением часового пояса.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Source))
            {
                errors.Add($"entityNotes[{index}]: source не может быть пустым.");
                continue;
            }

            result.Add(new(index, mediaType, mediaId, text, createdAt, item.Source));
        }

        return result;
    }

    private static List<ValidatedTasteNote> ValidateTasteNotes(
        IReadOnlyList<TasteNoteInput?> inputs, List<string> errors)
    {
        var result = new List<ValidatedTasteNote>();
        for (var index = 0; index < inputs.Count; index++)
        {
            var item = inputs[index];
            if (item is null)
            {
                errors.Add($"tasteNotes[{index}]: запись не может быть null.");
                continue;
            }

            var text = NormalizeText(item.Text ?? string.Empty);
            if (text.Length == 0)
            {
                errors.Add($"tasteNotes[{index}]: text не может быть пустым.");
                continue;
            }

            if (!TryParseDate(item.CreatedAt, out var createdAt))
            {
                errors.Add($"tasteNotes[{index}]: createdAt должен содержать ISO 8601 дату со смещением часового пояса.");
                continue;
            }

            if (string.IsNullOrWhiteSpace(item.Source))
            {
                errors.Add($"tasteNotes[{index}]: source не может быть пустым.");
                continue;
            }

            result.Add(new(index, text, createdAt, item.Source));
        }

        return result;
    }

    private static bool TryParseDate(string? value, out DateTimeOffset date)
    {
        date = default;
        if (string.IsNullOrWhiteSpace(value)) return false;
        if (value.EndsWith('z')) value = value[..^1] + "Z";
        return DateTimeOffset.TryParseExact(value, DateFormats, CultureInfo.InvariantCulture,
            DateTimeStyles.None, out date);
    }

    private static string NormalizeText(string text) =>
        text.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n').Trim();

    private static HashSet<Guid> ReadIds(SqliteConnection connection, SqliteTransaction transaction, string table)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = $"SELECT Id FROM {table}";
        using var reader = query.ExecuteReader();
        var ids = new HashSet<Guid>();
        while (reader.Read()) ids.Add(Guid.Parse(reader.GetString(0)));
        return ids;
    }

    private static HashSet<EntityDuplicateKey> ReadEntityKeys(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT MediaType,MediaId,Text,Source FROM AssistantNote";
        using var reader = query.ExecuteReader();
        var keys = new HashSet<EntityDuplicateKey>();
        while (reader.Read())
            keys.Add(new(Enum.Parse<MediaType>(reader.GetString(0)), Guid.Parse(reader.GetString(1)),
                NormalizeText(reader.GetString(2)), reader.GetString(3)));
        return keys;
    }

    private static HashSet<TasteDuplicateKey> ReadTasteKeys(SqliteConnection connection, SqliteTransaction transaction)
    {
        using var query = connection.CreateCommand();
        query.Transaction = transaction;
        query.CommandText = "SELECT Text,Source FROM TasteNote";
        using var reader = query.ExecuteReader();
        var keys = new HashSet<TasteDuplicateKey>();
        while (reader.Read()) keys.Add(new(NormalizeText(reader.GetString(0)), reader.GetString(1)));
        return keys;
    }

    private static void InsertEntityNote(SqliteConnection connection, SqliteTransaction transaction,
        ValidatedEntityNote note)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO AssistantNote(Id,MediaType,MediaId,Text,CreatedAt,Source) VALUES($id,$mediaType,$mediaId,$text,$createdAt,$source)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$mediaType", note.MediaType.ToString());
        command.Parameters.AddWithValue("$mediaId", note.MediaId.ToString());
        command.Parameters.AddWithValue("$text", note.Text);
        command.Parameters.AddWithValue("$createdAt", note.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$source", note.Source);
        command.ExecuteNonQuery();
    }

    private static void InsertTasteNote(SqliteConnection connection, SqliteTransaction transaction,
        ValidatedTasteNote note)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = "INSERT INTO TasteNote(Id,Text,CreatedAt,Source) VALUES($id,$text,$createdAt,$source)";
        command.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        command.Parameters.AddWithValue("$text", note.Text);
        command.Parameters.AddWithValue("$createdAt", note.CreatedAt.ToString("O", CultureInfo.InvariantCulture));
        command.Parameters.AddWithValue("$source", note.Source);
        command.ExecuteNonQuery();
    }

    private static ChatGptNotesImportResult Failure(params string[] errors) => Failure((IReadOnlyList<string>)errors);

    private static ChatGptNotesImportResult Failure(IReadOnlyList<string> errors) =>
        new(0, 0, 0, 0, errors);

    private sealed class NotesDocument
    {
        public int SchemaVersion { get; init; }
        public List<EntityNoteInput?>? EntityNotes { get; init; }
        public List<TasteNoteInput?>? TasteNotes { get; init; }
    }

    private sealed class EntityNoteInput
    {
        public string? MediaType { get; init; }
        public string? MediaId { get; init; }
        public string? Text { get; init; }
        public string? CreatedAt { get; init; }
        public string? Source { get; init; }
    }

    private sealed class TasteNoteInput
    {
        public string? Text { get; init; }
        public string? CreatedAt { get; init; }
        public string? Source { get; init; }
    }

    private sealed record ValidatedEntityNote(int Index, MediaType MediaType, Guid MediaId,
        string Text, DateTimeOffset CreatedAt, string Source);

    private sealed record ValidatedTasteNote(int Index, string Text, DateTimeOffset CreatedAt, string Source);

    private readonly record struct EntityDuplicateKey(MediaType MediaType, Guid MediaId, string Text, string Source);

    private readonly record struct TasteDuplicateKey(string Text, string Source);
}
