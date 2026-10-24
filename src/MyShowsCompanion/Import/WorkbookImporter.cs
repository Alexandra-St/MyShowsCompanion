using System.Security.Cryptography;
using Microsoft.Data.Sqlite;
using MyShowsCompanion.Domain;
using MyShowsCompanion.Storage;

namespace MyShowsCompanion.Import;

public sealed class WorkbookImporter(SqliteStore store)
{
    public ImportResult Import(string path)
    {
        var hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
        var parsed = new MyShowsWorkbookReader().Read(path);
        var warnings = parsed.Warnings.ToList();
        using var c = store.Open();
        using var tx = c.BeginTransaction();
        using (var q = c.CreateCommand())
        {
            q.Transaction = tx;
            q.CommandText = "SELECT 1 FROM ImportRun WHERE FileHash=$h LIMIT 1";
            q.Parameters.AddWithValue("$h", hash);
            if (q.ExecuteScalar() is not null)
            {
                var si = new ImportCounts(0, 0, parsed.Series.Count);
                var mi = new ImportCounts(0, 0, parsed.Movies.Count);
                var ei = new ImportCounts(0, 0, parsed.Episodes.Count);
                var run = new ImportRun(Guid.NewGuid(), DateTimeOffset.UtcNow, Path.GetFileName(path), hash,
                    0, 0, si.Skipped + mi.Skipped + ei.Skipped,
                    "Файл с таким SHA-256 уже импортирован; повторный импорт пропущен.");
                SaveRun(c, tx, run); tx.Commit(); return new(run, si, mi, ei);
            }
        }

        var existingSeries = store.GetSeries(c);
        var existingMovies = store.GetMovies(c);
        var existingEpisodes = store.GetEpisodes(c);
        var seriesResult = SyncSeries(c, tx, parsed.Series, existingSeries, warnings);
        var movieCounts = SyncMovies(c, tx, parsed.Movies, existingMovies, warnings);
        var episodeRows = ResolveEpisodeSeries(parsed.Episodes, parsed.Series, seriesResult.LocalIds);
        var episodeCounts = SyncEpisodes(c, tx, episodeRows, existingEpisodes, warnings);
        var totalI = seriesResult.Counts.Imported + movieCounts.Imported + episodeCounts.Imported;
        var totalU = seriesResult.Counts.Updated + movieCounts.Updated + episodeCounts.Updated;
        var totalS = seriesResult.Counts.Skipped + movieCounts.Skipped + episodeCounts.Skipped;
        var importRun = new ImportRun(Guid.NewGuid(), DateTimeOffset.UtcNow, Path.GetFileName(path), hash,
            totalI, totalU, totalS, string.Join("\n", warnings));
        SaveRun(c, tx, importRun);
        tx.Commit();
        return new(importRun, seriesResult.Counts, movieCounts, episodeCounts, CountUnresolved(warnings));
    }

    private static int CountUnresolved(IEnumerable<string> warnings)
    {
        var count = 0;
        foreach (var warning in warnings)
        {
            var marker = warning.LastIndexOf("unresolved ", StringComparison.OrdinalIgnoreCase);
            if (marker >= 0)
            {
                var number = new string(warning[(marker + 11)..].TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(number, out var n)) count += n;
            }
            else if ((marker = warning.LastIndexOf("без связи с сериалом: ", StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                var number = new string(warning[(marker + "без связи с сериалом: ".Length)..].TakeWhile(char.IsDigit).ToArray());
                if (int.TryParse(number, out var n)) count += n;
            }
            else if (warning.StartsWith("Неоднозначный эпизод", StringComparison.OrdinalIgnoreCase)) count++;
        }
        return count;
    }

    private sealed record SyncResult(ImportCounts Counts, Dictionary<Guid, Guid> LocalIds);

    private static SyncResult SyncSeries(SqliteConnection c, SqliteTransaction tx,
        IReadOnlyList<Series> incoming, IReadOnlyList<Series> existing, List<string> warnings)
    {
        var localIds = new Dictionary<Guid, Guid>();
        var inserted = 0; var updated = 0; var skipped = 0;
        foreach (var group in incoming.GroupBy(x => Key(x.Title)))
        {
            var old = existing.Where(x => Key(x.Title) == group.Key).ToArray();
            var rows = group.ToArray();
            if (old.Length == 0)
            {
                foreach (var row in rows) { InsertSeries(c, tx, row); localIds[row.Id] = row.Id; inserted++; }
                continue;
            }

            var pairs = new Dictionary<Guid, Series>();
            if (rows.Length == 1 && old.Length == 1) pairs[rows[0].Id] = old[0];
            else
            {
                var unmatchedRows = rows.ToList(); var unmatchedOld = old.ToList();
                PairByUniqueSignal(unmatchedRows, unmatchedOld, x => x.EpisodeDurationMinutes?.ToString(), pairs);
                foreach (var (sourceId, prior) in pairs) localIds[sourceId] = prior.Id;
                var pending = unmatchedRows.Count;
                skipped += pending;
                warnings.Add($"Неоднозначный сериал «{rows[0].Title}»: во входе {rows.Length}, в БД {old.Length}; сопоставлено {pairs.Count}, unresolved {pending}.");
            }

            foreach (var row in rows)
            {
                if (!pairs.TryGetValue(row.Id, out var prior))
                {
                    if (rows.Length == 1 && old.Length == 1) prior = old[0];
                    else continue;
                }
                localIds[row.Id] = prior.Id;
                if (Same(prior, row)) { skipped++; continue; }
                UpdateSeries(c, tx, prior.Id, row); updated++;
            }
        }
        return new(new(inserted, updated, skipped), localIds);
    }

    private static void PairByUniqueSignal(List<Series> incoming, List<Series> existing,
        Func<Series, string?> signal, Dictionary<Guid, Series> pairs)
    {
        var incomingGroups = incoming.Where(x => signal(x) is not null).GroupBy(signal).ToDictionary(g => g.Key!, g => g.ToArray());
        var existingGroups = existing.Where(x => signal(x) is not null).GroupBy(signal).ToDictionary(g => g.Key!, g => g.ToArray());
        foreach (var (key, ins) in incomingGroups)
        {
            if (ins.Length != 1 || !existingGroups.TryGetValue(key, out var olds) || olds.Length != 1) continue;
            pairs[ins[0].Id] = olds[0]; incoming.Remove(ins[0]); existing.Remove(olds[0]);
        }
    }

    private static string EpisodeKey(WatchedEpisode e) => $"{e.Season}:{e.EpisodeNumber}:{e.EpisodeTitle}";
    private static string Key(string s) => s.Trim().ToUpperInvariant();
    private static bool Same(Series a, Series b) => a.Status == b.Status && a.Rating == b.Rating &&
        a.EpisodeDurationMinutes == b.EpisodeDurationMinutes && a.WatchedEpisodeCount == b.WatchedEpisodeCount &&
        a.RemainingEpisodeCount == b.RemainingEpisodeCount && a.WatchedHours == b.WatchedHours && a.RemainingHours == b.RemainingHours;

    private static void InsertSeries(SqliteConnection c, SqliteTransaction tx, Series x)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "INSERT INTO Series VALUES($id,$title,$status,$rating,$duration,$watched,$remaining,$watchedHours,$remainingHours)";
        AddSeriesParams(q, x, x.Id); q.ExecuteNonQuery();
    }
    private static void UpdateSeries(SqliteConnection c, SqliteTransaction tx, Guid id, Series x)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "UPDATE Series SET Status=$status,Rating=$rating,EpisodeDurationMinutes=$duration,WatchedEpisodeCount=$watched,RemainingEpisodeCount=$remaining,WatchedHours=$watchedHours,RemainingHours=$remainingHours WHERE Id=$id";
        AddSeriesParams(q, x, id); q.ExecuteNonQuery();
    }
    private static void AddSeriesParams(SqliteCommand q, Series x, Guid id)
    {
        q.Parameters.AddWithValue("$id", id.ToString());
        if (q.CommandText.StartsWith("INSERT", StringComparison.Ordinal)) q.Parameters.AddWithValue("$title", x.Title);
        q.Parameters.AddWithValue("$status", x.Status); q.Parameters.AddWithValue("$rating", SqliteStore.Db(x.Rating));
        q.Parameters.AddWithValue("$duration", SqliteStore.Db(x.EpisodeDurationMinutes));
        q.Parameters.AddWithValue("$watched", SqliteStore.Db(x.WatchedEpisodeCount));
        q.Parameters.AddWithValue("$remaining", SqliteStore.Db(x.RemainingEpisodeCount));
        q.Parameters.AddWithValue("$watchedHours", SqliteStore.Db(x.WatchedHours));
        q.Parameters.AddWithValue("$remainingHours", SqliteStore.Db(x.RemainingHours));
    }

    private static ImportCounts SyncMovies(SqliteConnection c, SqliteTransaction tx,
        IReadOnlyList<Movie> incoming, IReadOnlyList<Movie> existing, List<string> warnings)
    {
        var inserted = 0; var updated = 0; var skipped = 0;
        foreach (var group in incoming.GroupBy(x => Key(x.Title)))
        {
            var rows = group.ToArray(); var old = existing.Where(x => Key(x.Title) == group.Key).ToArray();
            if (old.Length == 0) { foreach (var x in rows) { WriteMovie(c, tx, x, x.Id, insert: true); inserted++; } continue; }
            var pairs = new Dictionary<Guid, Movie>();
            if (rows.Length == 1 && old.Length == 1) pairs[rows[0].Id] = old[0];
            else
            {
                var ins = rows.ToList(); var olds = old.ToList();
                var ig = ins.Where(x => x.DurationMinutes.HasValue).GroupBy(x => x.DurationMinutes!.Value).ToDictionary(g => g.Key, g => g.ToArray());
                var og = olds.Where(x => x.DurationMinutes.HasValue).GroupBy(x => x.DurationMinutes!.Value).ToDictionary(g => g.Key, g => g.ToArray());
                foreach (var (duration, candidates) in ig)
                    if (candidates.Length == 1 && og.TryGetValue(duration, out var previous) && previous.Length == 1)
                    { pairs[candidates[0].Id] = previous[0]; ins.Remove(candidates[0]); olds.Remove(previous[0]); }
                skipped += ins.Count;
                warnings.Add($"Неоднозначный фильм «{rows[0].Title}»: во входе {rows.Length}, в БД {old.Length}; сопоставлено {pairs.Count}, unresolved {ins.Count}.");
            }
            foreach (var x in rows)
            {
                if (!pairs.TryGetValue(x.Id, out var prior)) { if (rows.Length == 1 && old.Length == 1) prior = old[0]; else continue; }
                if (prior.Status == x.Status && prior.Rating == x.Rating && prior.DurationMinutes == x.DurationMinutes && prior.WatchedDate == x.WatchedDate) { skipped++; continue; }
                WriteMovie(c, tx, x, prior.Id, insert: false); updated++;
            }
        }
        return new(inserted, updated, skipped);
    }

    private static void WriteMovie(SqliteConnection c, SqliteTransaction tx, Movie x, Guid id, bool insert)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = insert ? "INSERT INTO Movie VALUES($id,$title,$status,$rating,$duration,$date)" :
            "UPDATE Movie SET Status=$status,Rating=$rating,DurationMinutes=$duration,WatchedDate=$date WHERE Id=$id";
        q.Parameters.AddWithValue("$id", id.ToString());
        if (insert) q.Parameters.AddWithValue("$title", x.Title);
        q.Parameters.AddWithValue("$status", x.Status); q.Parameters.AddWithValue("$rating", SqliteStore.Db(x.Rating));
        q.Parameters.AddWithValue("$duration", SqliteStore.Db(x.DurationMinutes));
        q.Parameters.AddWithValue("$date", SqliteStore.Db(x.WatchedDate?.ToString("yyyy-MM-dd")));
        q.ExecuteNonQuery();
    }

    private static WatchedEpisode[] ResolveEpisodeSeries(IReadOnlyList<WatchedEpisode> episodes,
        IReadOnlyList<Series> series, IReadOnlyDictionary<Guid, Guid> localIds)
    {
        var result = new List<WatchedEpisode>();
        var byTitle = series.GroupBy(s => Key(s.Title)).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var e in episodes)
        {
            var candidates = byTitle.GetValueOrDefault(Key(e.SeriesTitleFromSource), []);
            Guid? resolved = candidates.Length == 1 && localIds.TryGetValue(candidates[0].Id, out var localId) ? localId : null;
            result.Add(e with { SeriesId = resolved });
        }
        return result.ToArray();
    }

    private static ImportCounts SyncEpisodes(SqliteConnection c, SqliteTransaction tx,
        IReadOnlyList<WatchedEpisode> incoming, IReadOnlyList<WatchedEpisode> existing, List<string> warnings)
    {
        var inserted = 0; var updated = 0; var skipped = 0;
        foreach (var unlinked in incoming.Where(e => e.SeriesId is null).GroupBy(e => Key(e.SeriesTitleFromSource)))
            warnings.Add($"Эпизоды «{unlinked.First().SeriesTitleFromSource}» без связи с сериалом: {unlinked.Count()}.");
        string Stable(WatchedEpisode e) => $"{Key(e.SeriesTitleFromSource)}|{EpisodeKey(e)}";
        var existingByKey = existing.GroupBy(Stable).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var group in incoming.GroupBy(Stable))
        {
            var rows = group.ToArray(); var old = existingByKey.GetValueOrDefault(group.Key, []);
            if (old.Length == 0) { foreach (var e in rows) { WriteEpisode(c, tx, e, e.Id, true); inserted++; } continue; }
            if (rows.Length == 1 && old.Length == 1)
            {
                var e = rows[0]; var prior = old[0];
                if (e.WatchedDate == prior.WatchedDate && (e.SeriesId is null || e.SeriesId == prior.SeriesId)) { skipped++; continue; }
                if (e.SeriesId is not null && prior.SeriesId is not null && e.SeriesId != prior.SeriesId)
                { skipped++; warnings.Add($"Неоднозначная связь эпизода «{e.SeriesTitleFromSource}» S{e.Season}E{e.EpisodeNumber}; local ID сериала не изменён."); continue; }
                WriteEpisode(c, tx, e, prior.Id, false); updated++; continue;
            }
            // Preserve multiplicity for duplicate/re-watch rows. Match exact dates first;
            // only a one-to-one leftover may be treated as a changed date.
            var leftovers = old.ToList(); var incomingLeftovers = new List<WatchedEpisode>();
            foreach (var e in rows)
            {
                var exact = leftovers.FirstOrDefault(x => x.WatchedDate == e.WatchedDate);
                if (exact is not null) { leftovers.Remove(exact); skipped++; }
                else incomingLeftovers.Add(e);
            }
            if (leftovers.Count == 1 && incomingLeftovers.Count == 1 &&
                (incomingLeftovers[0].SeriesId is null || leftovers[0].SeriesId is null || incomingLeftovers[0].SeriesId == leftovers[0].SeriesId))
            { WriteEpisode(c, tx, incomingLeftovers[0], leftovers[0].Id, false); updated++; }
            else if (incomingLeftovers.Count > 0)
            { skipped += incomingLeftovers.Count; warnings.Add($"Неоднозначный эпизод «{rows[0].SeriesTitleFromSource}» S{rows[0].Season}E{rows[0].EpisodeNumber}: осталось несколько возможных записей."); }
        }
        return new(inserted, updated, skipped);
    }

    private static void WriteEpisode(SqliteConnection c, SqliteTransaction tx, WatchedEpisode e, Guid id, bool insert)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = insert ? "INSERT INTO WatchedEpisode VALUES($id,$series,$source,$season,$number,$title,$date)" :
            "UPDATE WatchedEpisode SET SeriesId=COALESCE($series,SeriesId),WatchedDate=$date WHERE Id=$id";
        q.Parameters.AddWithValue("$id", id.ToString()); q.Parameters.AddWithValue("$series", SqliteStore.Db(e.SeriesId?.ToString()));
        if (insert) { q.Parameters.AddWithValue("$source", e.SeriesTitleFromSource); q.Parameters.AddWithValue("$season", SqliteStore.Db(e.Season)); q.Parameters.AddWithValue("$number", SqliteStore.Db(e.EpisodeNumber)); q.Parameters.AddWithValue("$title", SqliteStore.Db(e.EpisodeTitle)); }
        q.Parameters.AddWithValue("$date", SqliteStore.Db(e.WatchedDate?.ToString("yyyy-MM-dd"))); q.ExecuteNonQuery();
    }

    private static void SaveRun(SqliteConnection c, SqliteTransaction tx, ImportRun run)
    {
        using var q = c.CreateCommand(); q.Transaction = tx;
        q.CommandText = "INSERT INTO ImportRun VALUES($id,$at,$file,$hash,$i,$u,$s,$w)";
        q.Parameters.AddWithValue("$id", run.Id.ToString()); q.Parameters.AddWithValue("$at", run.ImportedAt.ToString("O"));
        q.Parameters.AddWithValue("$file", run.SourceFilename); q.Parameters.AddWithValue("$hash", run.FileHash);
        q.Parameters.AddWithValue("$i", run.ImportedCount); q.Parameters.AddWithValue("$u", run.UpdatedCount);
        q.Parameters.AddWithValue("$s", run.SkippedCount); q.Parameters.AddWithValue("$w", run.ErrorsAndWarnings); q.ExecuteNonQuery();
    }
}
