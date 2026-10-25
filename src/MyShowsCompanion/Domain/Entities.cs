namespace MyShowsCompanion.Domain;

public enum MediaType
{
    Series,
    Movie
}

public sealed record Series(Guid Id, string Title, string Status, decimal? Rating,
    int? EpisodeDurationMinutes, int? WatchedEpisodeCount, int? RemainingEpisodeCount,
    decimal? WatchedHours = null, decimal? RemainingHours = null);

public sealed record Movie(Guid Id, string Title, string Status, decimal? Rating,
    int? DurationMinutes, DateOnly? WatchedDate);

public sealed record WatchedEpisode(Guid Id, Guid? SeriesId, string SeriesTitleFromSource,
    int? Season, int? EpisodeNumber, string? EpisodeTitle, DateOnly? WatchedDate);

public sealed record ImportRun(Guid Id, DateTimeOffset ImportedAt, string SourceFilename,
    string FileHash, int ImportedCount, int UpdatedCount, int SkippedCount,
    string ErrorsAndWarnings);

public sealed record AssistantNote(Guid Id, MediaType MediaType, Guid MediaId, string Text,
    DateTimeOffset CreatedAt, string Source);

public sealed record TasteNote(Guid Id, string Text, DateTimeOffset CreatedAt, string Source);
