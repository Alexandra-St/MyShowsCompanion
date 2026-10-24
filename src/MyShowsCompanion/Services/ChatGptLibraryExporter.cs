using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Encodings.Web;
using MyShowsCompanion.Storage;

namespace MyShowsCompanion.Services;

public sealed record ChatGptLibraryExport(
    int SchemaVersion,
    DateTimeOffset GeneratedAt,
    ChatGptExportStatistics Statistics,
    IReadOnlyList<ChatGptSeries> Series,
    IReadOnlyList<ChatGptMovie> Movies);

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
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceStatus = null);

public sealed record ChatGptMovie(
    Guid Id,
    string Title,
    string Status,
    decimal? Rating,
    DateOnly? WatchedDate,
    int? DurationMinutes,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? SourceStatus = null);

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
        var series = store.GetSeries(connection)
            .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id)
            .Select(x =>
            {
                var (status, sourceStatus) = NormalizeSeriesStatus(x.Status);
                return new ChatGptSeries(x.Id, x.Title, status, x.Rating, x.WatchedEpisodeCount, x.RemainingEpisodeCount, sourceStatus);
            }).ToArray();
        var movies = store.GetMovies(connection)
            .OrderBy(x => x.Title, StringComparer.OrdinalIgnoreCase).ThenBy(x => x.Id)
            .Select(x =>
            {
                var (status, sourceStatus) = NormalizeMovieStatus(x.Status);
                return new ChatGptMovie(x.Id, x.Title, status, x.Rating, x.WatchedDate, x.DurationMinutes, sourceStatus);
            }).ToArray();

        var statistics = new ChatGptExportStatistics(
            series.Length,
            movies.Length,
            series.Count(x => x.Rating.HasValue),
            movies.Count(x => x.Rating.HasValue),
            CountStatuses(series.Select(x => x.Status), "watching", "completed", "dropped", "planned", "unknown"),
            CountStatuses(movies.Select(x => x.Status), "completed", "planned", "unknown"));

        return new ChatGptLibraryExport(1, generatedAt ?? DateTimeOffset.UtcNow, statistics, series, movies);
    }

    public static byte[] Serialize(ChatGptLibraryExport export) => JsonSerializer.SerializeToUtf8Bytes(export, JsonOptions);

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
