using Microsoft.Data.Sqlite;
using MyShowsCompanion.Storage;

namespace MyShowsCompanion.Tests;

public sealed class LocalDatabaseBootstrapperTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString("N"));

    public LocalDatabaseBootstrapperTests() => Directory.CreateDirectory(_directory);
    public void Dispose() => Directory.Delete(_directory, true);

    [Fact]
    public void CopiesAndValidatesLegacyDatabaseWithoutRemovingIt()
    {
        var legacyPath = Path.Combine(_directory, "legacy.sqlite3");
        Seed(legacyPath, "Original");
        var support = Path.Combine(_directory, "Application Support", "MyShowsCompanion");

        var migratedPath = LocalDatabaseBootstrapper.EnsureDatabase(support, legacyPath);

        Assert.Equal(Path.Combine(support, "MyShowsCompanion.sqlite3"), migratedPath);
        Assert.True(File.Exists(legacyPath));
        using var migrated = new SqliteStore(migratedPath).OpenReadOnly();
        Assert.Equal("Original", ReadSingleTitle(migrated));
        Assert.Equal(1, CountRows(migrated, "Series"));
        Assert.Equal(1, CountRows(migrated, "Movie"));
        Assert.Equal(1, CountRows(migrated, "WatchedEpisode"));
        Assert.Equal(1, CountRows(migrated, "ImportRun"));
    }

    [Fact]
    public void DoesNotOverwriteExistingApplicationSupportDatabase()
    {
        var legacyPath = Path.Combine(_directory, "legacy.sqlite3");
        var support = Path.Combine(_directory, "Application Support", "MyShowsCompanion");
        Seed(legacyPath, "Legacy");
        var supportPath = Path.Combine(support, "MyShowsCompanion.sqlite3");
        Seed(supportPath, "Existing");

        var selectedPath = LocalDatabaseBootstrapper.EnsureDatabase(support, legacyPath);

        Assert.Equal(supportPath, selectedPath);
        using var current = new SqliteStore(selectedPath).OpenReadOnly();
        Assert.Equal("Existing", ReadSingleTitle(current));
        Assert.True(File.Exists(legacyPath));
    }

    private static void Seed(string path, string title)
    {
        var store = new SqliteStore(path);
        store.Initialize();
        using var connection = store.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO Series(Id,Title,Status) VALUES ('series-id',$title,'Смотрю');
            INSERT INTO Movie(Id,Title,Status) VALUES ('movie-id','Film','Буду смотреть');
            INSERT INTO WatchedEpisode(Id,SeriesId,SeriesTitleFromSource,Season,EpisodeNumber) VALUES ('episode-id','series-id',$title,1,1);
            INSERT INTO ImportRun(Id,ImportedAt,SourceFilename,FileHash,ImportedCount,UpdatedCount,SkippedCount,ErrorsAndWarnings)
            VALUES ('run-id','2026-10-24T10:00:00Z','export.xlsx','hash',1,0,0,'');
            """;
        command.Parameters.AddWithValue("$title", title);
        command.ExecuteNonQuery();
    }

    private static string ReadSingleTitle(SqliteConnection connection)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT Title FROM Series;";
        return (string)command.ExecuteScalar()!;
    }

    private static int CountRows(SqliteConnection connection, string table)
    {
        using var command = connection.CreateCommand();
        command.CommandText = $"SELECT COUNT(*) FROM {table};";
        return Convert.ToInt32(command.ExecuteScalar());
    }
}
