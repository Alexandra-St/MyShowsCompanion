using System.Text.Json;
using MyShowsCompanion.Services;
using MyShowsCompanion.Storage;

namespace MyShowsCompanion.Tests;

public sealed class ChatGptLibraryExporterTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));
    private readonly SqliteStore _store;

    public ChatGptLibraryExporterTests()
    {
        Directory.CreateDirectory(_dir);
        _store = new SqliteStore(Path.Combine(_dir, "library.sqlite3"));
        _store.Initialize();
        Seed();
    }

    public void Dispose() => Directory.Delete(_dir, true);

    [Fact]
    public void ExportsCompactVersionedJsonWithNormalizedStatusesNullablesDuplicatesAndStatistics()
    {
        var instant = new DateTimeOffset(2026, 10, 24, 12, 30, 0, TimeSpan.Zero);
        var export = ChatGptLibraryExporter.Read(_store, instant);
        var json = ChatGptLibraryExporter.Serialize(export);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        var series = root.GetProperty("series").EnumerateArray().ToArray();
        var movies = root.GetProperty("movies").EnumerateArray().ToArray();
        var statistics = root.GetProperty("statistics");

        Assert.Equal(1, root.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(instant, root.GetProperty("generatedAt").GetDateTimeOffset());
        Assert.Equal(5, series.Length);
        Assert.Equal(3, movies.Length);
        Assert.Equal(5, statistics.GetProperty("totalSeries").GetInt32());
        Assert.Equal(3, statistics.GetProperty("totalMovies").GetInt32());
        Assert.Equal(2, statistics.GetProperty("ratedSeries").GetInt32());
        Assert.Equal(2, statistics.GetProperty("ratedMovies").GetInt32());
        Assert.Equal(1, statistics.GetProperty("seriesByStatus").GetProperty("watching").GetInt32());
        Assert.Equal(1, statistics.GetProperty("seriesByStatus").GetProperty("completed").GetInt32());
        Assert.Equal(1, statistics.GetProperty("seriesByStatus").GetProperty("dropped").GetInt32());
        Assert.Equal(1, statistics.GetProperty("seriesByStatus").GetProperty("planned").GetInt32());
        Assert.Equal(1, statistics.GetProperty("seriesByStatus").GetProperty("unknown").GetInt32());
        Assert.Equal(1, statistics.GetProperty("moviesByStatus").GetProperty("completed").GetInt32());
        Assert.Equal(1, statistics.GetProperty("moviesByStatus").GetProperty("planned").GetInt32());
        Assert.Equal(1, statistics.GetProperty("moviesByStatus").GetProperty("unknown").GetInt32());

        Assert.Equal(new[] { "Alpha", "Alpha", "Dropped", "Planned", "Зулу 世界" }, series.Select(x => x.GetProperty("title").GetString()));
        Assert.Equal("00000000-0000-0000-0000-000000000001", series[0].GetProperty("id").GetString());
        Assert.Equal("00000000-0000-0000-0000-000000000002", series[1].GetProperty("id").GetString());
        Assert.Equal("watching", series[0].GetProperty("status").GetString());
        Assert.True(series[0].GetProperty("rating").ValueKind == JsonValueKind.Null);
        Assert.Equal("unknown", series[4].GetProperty("status").GetString());
        Assert.Equal("Paused by user", series[4].GetProperty("sourceStatus").GetString());

        Assert.Equal("planned", movies[0].GetProperty("status").GetString());
        Assert.True(movies[0].GetProperty("rating").ValueKind == JsonValueKind.Null);
        Assert.True(movies[0].GetProperty("watchedDate").ValueKind == JsonValueKind.Null);
        Assert.Equal("unknown", movies[1].GetProperty("status").GetString());
        Assert.Equal("On hold", movies[1].GetProperty("sourceStatus").GetString());

        Assert.False(root.TryGetProperty("episodes", out _));
        Assert.All(series, item => Assert.False(item.TryGetProperty("episodeList", out _)));
        Assert.All(movies, item => Assert.False(item.TryGetProperty("episodeList", out _)));
        Assert.Contains("Зулу 世界", System.Text.Encoding.UTF8.GetString(json));
    }

    private void Seed()
    {
        using var c = _store.Open();
        using var command = c.CreateCommand();
        command.CommandText = """
            INSERT INTO Series(Id,Title,Status,Rating,EpisodeDurationMinutes,WatchedEpisodeCount,RemainingEpisodeCount)
            VALUES ($id1,'Alpha','Смотрю',NULL,42,2,8),
                   ($id2,'Alpha','Полностью посмотрел',4.5,45,10,0),
                   ($id3,'Зулу 世界','Paused by user',NULL,NULL,NULL,NULL),
                   ($id4,'Dropped','Перестал',3.5,NULL,NULL,NULL),
                   ($id5,'Planned','Буду смотреть',NULL,NULL,NULL,NULL);
            INSERT INTO Movie(Id,Title,Status,Rating,DurationMinutes,WatchedDate)
            VALUES ($movie1,'A Film','Буду смотреть',NULL,100,NULL),
                   ($movie2,'B Film','On hold',3.5,90,'2024-02-03'),
                   ($movie3,'C Film','Полностью посмотрел',4,110,'2024-01-02');
            INSERT INTO WatchedEpisode(Id,SeriesId,SeriesTitleFromSource,Season,EpisodeNumber,EpisodeTitle,WatchedDate)
            VALUES ($episode,$id1,'Alpha',1,1,'Pilot','2024-01-01');
            """;
        command.Parameters.AddWithValue("$id1", Guid.Parse("00000000-0000-0000-0000-000000000001").ToString());
        command.Parameters.AddWithValue("$id2", Guid.Parse("00000000-0000-0000-0000-000000000002").ToString());
        command.Parameters.AddWithValue("$id3", Guid.Parse("00000000-0000-0000-0000-000000000003").ToString());
        command.Parameters.AddWithValue("$id4", Guid.Parse("00000000-0000-0000-0000-000000000006").ToString());
        command.Parameters.AddWithValue("$id5", Guid.Parse("00000000-0000-0000-0000-000000000007").ToString());
        command.Parameters.AddWithValue("$movie1", Guid.Parse("00000000-0000-0000-0000-000000000004").ToString());
        command.Parameters.AddWithValue("$movie2", Guid.Parse("00000000-0000-0000-0000-000000000005").ToString());
        command.Parameters.AddWithValue("$movie3", Guid.Parse("00000000-0000-0000-0000-000000000008").ToString());
        command.Parameters.AddWithValue("$episode", Guid.NewGuid().ToString());
        command.ExecuteNonQuery();
    }
}
