using MyShowsCompanion.Domain;
using MyShowsCompanion.Storage;

namespace MyShowsCompanion.Services;

public enum CatalogSort { Title, Rating }

public static class LibraryQueries
{
    public static IReadOnlyList<Series> FilterSeries(IEnumerable<Series> source, string? search,
        string? status, bool ratedOnly, CatalogSort sort)
    {
        var query=source.AsEnumerable();
        if(!string.IsNullOrWhiteSpace(search)) query=query.Where(x=>x.Title.Contains(search.Trim(),StringComparison.OrdinalIgnoreCase));
        if(!string.IsNullOrWhiteSpace(status) && status!="Все статусы") query=query.Where(x=>x.Status==status);
        if(ratedOnly) query=query.Where(x=>x.Rating.HasValue);
        return sort==CatalogSort.Rating ? query.OrderByDescending(x=>x.Rating).ThenBy(x=>x.Title,StringComparer.CurrentCultureIgnoreCase).ToArray() : query.OrderBy(x=>x.Title,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }

    public static IReadOnlyList<Movie> FilterMovies(IEnumerable<Movie> source, string? search,
        string? status, bool ratedOnly, CatalogSort sort)
    {
        var query=source.AsEnumerable();
        if(!string.IsNullOrWhiteSpace(search)) query=query.Where(x=>x.Title.Contains(search.Trim(),StringComparison.OrdinalIgnoreCase));
        if(!string.IsNullOrWhiteSpace(status) && status!="Все статусы") query=query.Where(x=>x.Status==status);
        if(ratedOnly) query=query.Where(x=>x.Rating.HasValue);
        return sort==CatalogSort.Rating ? query.OrderByDescending(x=>x.Rating).ThenBy(x=>x.Title,StringComparer.CurrentCultureIgnoreCase).ToArray() : query.OrderBy(x=>x.Title,StringComparer.CurrentCultureIgnoreCase).ToArray();
    }
}

public sealed record LibrarySnapshot(IReadOnlyList<Series> Series, IReadOnlyList<Movie> Movies,
    int WatchedEpisodes, DateTimeOffset? LastImport, IReadOnlyList<(string Message,int Count)> Unresolved,
    int UnresolvedCount, int UnlinkedEpisodeCount, int AmbiguousSeriesCount, int AmbiguousMovieCount);

public sealed class LibraryService(SqliteStore store)
{
    public LibrarySnapshot Load()
    {
        store.Initialize();
        using var c=store.Open();
        var series=store.GetSeries(c);var movies=store.GetMovies(c);var episodes=store.GetEpisodes(c);
        var unresolved=store.GetUnresolvedWarnings(c);
        var unlinkedEpisodes=unresolved.Where(x=>x.Message.StartsWith("Эпизоды «",StringComparison.Ordinal)).Sum(x=>x.Count);
        var ambiguousSeries=unresolved.Where(x=>x.Message.StartsWith("Сериал «",StringComparison.Ordinal)).Sum(x=>x.Count);
        var ambiguousMovies=unresolved.Where(x=>x.Message.StartsWith("Фильм «",StringComparison.Ordinal)).Sum(x=>x.Count);
        return new(series,movies,episodes.Count,store.GetLatestImportRun(c)?.ImportedAt,unresolved,unresolved.Sum(x=>x.Count),unlinkedEpisodes,ambiguousSeries,ambiguousMovies);
    }
}
