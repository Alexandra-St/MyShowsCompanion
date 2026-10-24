using MyShowsCompanion.Domain;

namespace MyShowsCompanion.Import;

public sealed record ImportCounts(int Imported, int Updated, int Skipped);
public sealed record ParsedWorkbook(IReadOnlyList<Series> Series, IReadOnlyList<Movie> Movies,
    IReadOnlyList<WatchedEpisode> Episodes, IReadOnlyList<string> Warnings);
public sealed record ImportResult(ImportRun Run, ImportCounts SeriesCounts,
    ImportCounts MovieCounts, ImportCounts EpisodeCounts, int UnresolvedCount = 0);
