using Microsoft.Data.Sqlite;

namespace MyShowsCompanion.Storage;

public static class LocalDatabaseBootstrapper
{
    private static readonly string[] RequiredTables = ["Series", "Movie", "WatchedEpisode", "ImportRun"];

    /// <summary>Returns the Application Support database path, copying a legacy database once when needed.</summary>
    public static string EnsureDatabase(string applicationSupportDirectory, string? legacyDatabasePath)
    {
        Directory.CreateDirectory(applicationSupportDirectory);
        var targetPath = Path.Combine(applicationSupportDirectory, "MyShowsCompanion.sqlite3");
        if (File.Exists(targetPath)) return targetPath;
        if (string.IsNullOrWhiteSpace(legacyDatabasePath) || !File.Exists(legacyDatabasePath)) return targetPath;

        var temporaryPath = Path.Combine(applicationSupportDirectory,
            $"MyShowsCompanion.migration-{Guid.NewGuid():N}.sqlite3");
        try
        {
            var sourceBuilder = new SqliteConnectionStringBuilder
            {
                DataSource = legacyDatabasePath,
                Mode = SqliteOpenMode.ReadOnly
            };
            using (var source = new SqliteConnection(sourceBuilder.ToString()))
            {
                source.Open();
                var targetBuilder = new SqliteConnectionStringBuilder
                {
                    DataSource = temporaryPath,
                    Mode = SqliteOpenMode.ReadWriteCreate
                };
                using var target = new SqliteConnection(targetBuilder.ToString());
                target.Open();
                source.BackupDatabase(target);
                ValidateCopiedDatabase(target);
            }

            // Same-directory move is atomic and refuses to overwrite an existing destination.
            File.Move(temporaryPath, targetPath, overwrite: false);
            return targetPath;
        }
        catch (IOException) when (File.Exists(targetPath))
        {
            // Another app instance completed the one-time migration while this one was copying.
            TryDelete(temporaryPath);
            return targetPath;
        }
        catch
        {
            TryDelete(temporaryPath);
            throw;
        }
    }

    private static void ValidateCopiedDatabase(SqliteConnection connection)
    {
        using (var integrity = connection.CreateCommand())
        {
            integrity.CommandText = "PRAGMA integrity_check;";
            var result = Convert.ToString(integrity.ExecuteScalar());
            if (!string.Equals(result, "ok", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException($"SQLite integrity check failed: {result}");
        }

        foreach (var table in RequiredTables)
        {
            using var query = connection.CreateCommand();
            query.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name=$name;";
            query.Parameters.AddWithValue("$name", table);
            if (Convert.ToInt32(query.ExecuteScalar()) != 1)
                throw new InvalidDataException($"Migrated database is missing the {table} table.");
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { /* Keep the original database safe. */ }
        try { if (File.Exists(path + "-wal")) File.Delete(path + "-wal"); } catch { }
        try { if (File.Exists(path + "-shm")) File.Delete(path + "-shm"); } catch { }
    }
}
