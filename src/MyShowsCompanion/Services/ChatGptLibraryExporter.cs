using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using Microsoft.Data.Sqlite;
using MyShowsCompanion.Domain;
using MyShowsCompanion.Storage;

namespace MyShowsCompanion.Services;

public sealed record ChatGptLibraryExport(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    ChatGptExportStatistics Statistics,
    IReadOnlyList<ChatGptSeries> Series,
    IReadOnlyList<ChatGptMovie> Movies,
    IReadOnlyList<ChatGptTasteNote> TasteNotes);

public sealed record ChatGptExportStatistics(
    int TotalSeries,
    int TotalMovies,
    int RatedSeries,
    int RatedMovies,
    IReadOnlyDictionary<string, int> SeriesByStatus,
    IReadOnlyDictionary<string, int> MoviesByStatus);

public sealed record ChatGptSeries(
    Guid Id,
    string Title,
    string Status,
    decimal? Rating,
    int? WatchedEpisodes,
    int? RemainingEpisodes,
    IReadOnlyList<ChatGptNote> Notes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceStatus = null);

public sealed record ChatGptMovie(
    Guid Id,
    string Title,
    string Status,
    decimal? Rating,
    DateOnly? WatchedDate,
    int? DurationMinutes,
    IReadOnlyList<ChatGptNote> Notes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceStatus = null);

public sealed record ChatGptNote(string Text, DateTimeOffset CreatedAt, string Source);

public sealed record ChatGptTasteNote(string Text, DateTimeOffset CreatedAt, string Source);

public static class ChatGptLibraryExporter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static ChatGptLibraryExport Read(SqliteStore store, DateTimeOffset? generatedAt = null)
    {
        using var connection = store.OpenReadOnly();
        var notesByMedia = LoadAssistantNotes(store, connection);
        var series = store.GetSeries(connection)
            .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id)
            .Select(x =>
            {
                var (status, sourceStatus) = NormalizeSeriesStatus(x.Status);
                return new ChatGptSeries(x.Id, x.Title, status, x.Rating, x.WatchedEpisodeCount,
                    x.RemainingEpisodeCount, notesByMedia.GetValueOrDefault((MediaType.Series, x.Id), []), sourceStatus);
            }).ToArray();
        var movies = store.GetMovies(connection)
            .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id)
            .Select(x =>
            {
                var (status, sourceStatus) = NormalizeMovieStatus(x.Status);
                return new ChatGptMovie(x.Id, x.Title, status, x.Rating, x.WatchedDate, x.DurationMinutes,
                    notesByMedia.GetValueOrDefault((MediaType.Movie, x.Id), []), sourceStatus);
            }).ToArray();

        var statistics = new ChatGptExportStatistics(
            series.Length,
            movies.Length,
            series.Count(x => x.Rating.HasValue),
            movies.Count(x => x.Rating.HasValue),
            CountStatuses(series.Select(x => x.Status), "watching", "completed", "dropped", "planned", "unknown"),
            CountStatuses(movies.Select(x => x.Status), "completed", "planned", "unknown"));

        var tasteNotes = store.GetTasteNotes(connection)
            .Select(x => new ChatGptTasteNote(x.Text, x.CreatedAt, x.Source)).ToArray();

        return new ChatGptLibraryExport(2, generatedAt ?? DateTimeOffset.UtcNow, statistics, series, movies, tasteNotes);
    }

    public static byte[] Serialize(ChatGptLibraryExport export) => JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions);

    private static Dictionary<(MediaType MediaType, Guid MediaId), ChatGptNote[]> LoadAssistantNotes(SqliteStore store, SqliteConnection connection) =>
        store.GetAssistantNotes(connection)
            .GroupBy(x => (x.MediaType, x.MediaId))
            .ToDictionary(g => g.Key, g => g.Select(x => new ChatGptNote(x.Text, x.CreatedAt, x.Source)).ToArray());

    private static (string Status, string? SourceStatus) NormalizeSeriesStatus(string value) => Normalize(value, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Смотрю"] = "watching",
        ["Полностью посмотрел"] = "completed",
        ["Перестал"] = "dropped",
        ["Буду смотреть"] = "planned"
    });

    private static (string Status, string? SourceStatus) NormalizeMovieStatus(string value) => Normalize(value, new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["Полностью посмотрел"] = "completed",
        ["Буду смотреть"] = "planned"
    });

    private static (string Status, string? SourceStatus) Normalize(string value, IReadOnlyDictionary<string, string> known)
    {
        var sourceStatus = value.Trim();
        return known.TryGetValue(sourceStatus, out var normalized)
            ? (normalized, null)
            : ("unknown", sourceStatus);
    }

    private static IReadOnlyDictionary<string, int> CountStatuses(IEnumerable<string> statuses, params string[] values)
    {
        var counts = statuses.GroupBy(x => x).ToDictionary(x => x.Key, x => x.Count(), StringComparer.Ordinal);
        return values.ToDictionary(x => x, x => counts.GetValueOrDefault(x), StringComparer.Ordinal);
    }
}
