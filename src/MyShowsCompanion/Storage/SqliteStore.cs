using Microsoft.Data.Sqlite;
using MyShowsCompanion.Domain;

namespace MyShowsCompanion.Storage;

public sealed class SqliteStore(string databasePath)
{
    private string ConnectionString => new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    public SqliteConnection Open()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        var c = new SqliteConnection(ConnectionString); c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON; PRAGMA journal_mode=WAL;"; cmd.ExecuteNonQuery();
        return c;
    }
    public SqliteConnection OpenReadOnly()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadOnly
        }.ToString();
        var c = new SqliteConnection(connectionString);
        c.Open();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "PRAGMA foreign_keys=ON;";
        cmd.ExecuteNonQuery();
        return c;
    }
    public void Initialize()
    {
        using var c = Open(); using var cmd = c.CreateCommand();
        cmd.CommandText = """
        CREATE TABLE IF NOT EXISTS Series(Id TEXT PRIMARY KEY, Title TEXT NOT NULL, Status TEXT NOT NULL, Rating REAL NULL, EpisodeDurationMinutes INTEGER NULL, WatchedEpisodeCount INTEGER NULL, RemainingEpisodeCount INTEGER NULL, WatchedHours REAL NULL, RemainingHours REAL NULL);
        CREATE TABLE IF NOT EXISTS Movie(Id TEXT PRIMARY KEY, Title TEXT NOT NULL, Status TEXT NOT NULL, Rating REAL NULL, DurationMinutes INTEGER NULL, WatchedDate TEXT NULL);
        CREATE TABLE IF NOT EXISTS WatchedEpisode(Id TEXT PRIMARY KEY, SeriesId TEXT NULL REFERENCES Series(Id), SeriesTitleFromSource TEXT NOT NULL, Season INTEGER NULL, EpisodeNumber INTEGER NULL, EpisodeTitle TEXT NULL, WatchedDate TEXT NULL);
        CREATE TABLE IF NOT EXISTS ImportRun(Id TEXT PRIMARY KEY, ImportedAt TEXT NOT NULL, SourceFilename TEXT NOT NULL, FileHash TEXT NOT NULL, ImportedCount INTEGER NOT NULL, UpdatedCount INTEGER NOT NULL, SkippedCount INTEGER NOT NULL, ErrorsAndWarnings TEXT NOT NULL);
        CREATE INDEX IF NOT EXISTS IX_Series_Title ON Series(Title); CREATE INDEX IF NOT EXISTS IX_Movie_Title ON Movie(Title);
        CREATE INDEX IF NOT EXISTS IX_Episode_Lookup ON WatchedEpisode(SeriesTitleFromSource, Season, EpisodeNumber);
        """; cmd.ExecuteNonQuery();
        EnsureColumn(c, "Series", "WatchedHours", "REAL NULL");
        EnsureColumn(c, "Series", "RemainingHours", "REAL NULL");
    }
    private static void EnsureColumn(SqliteConnection c,string table,string column,string type){using var q=c.CreateCommand();q.CommandText=$"PRAGMA table_info({table})";using var r=q.ExecuteReader();while(r.Read())if(r.GetString(1)==column)return;r.Close();using var alter=c.CreateCommand();alter.CommandText=$"ALTER TABLE {table} ADD COLUMN {column} {type}";alter.ExecuteNonQuery();}
    public List<Series> GetSeries(SqliteConnection c) { using var q=c.CreateCommand(); q.CommandText="SELECT Id,Title,Status,Rating,EpisodeDurationMinutes,WatchedEpisodeCount,RemainingEpisodeCount,WatchedHours,RemainingHours FROM Series"; using var r=q.ExecuteReader(); var x=new List<Series>(); while(r.Read()) x.Add(new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),NDec(r,3),NInt(r,4),NInt(r,5),NInt(r,6),NDec(r,7),NDec(r,8))); return x; }
    public List<Movie> GetMovies(SqliteConnection c) { using var q=c.CreateCommand(); q.CommandText="SELECT * FROM Movie"; using var r=q.ExecuteReader(); var x=new List<Movie>(); while(r.Read()) x.Add(new(Guid.Parse(r.GetString(0)),r.GetString(1),r.GetString(2),NDec(r,3),NInt(r,4),r.IsDBNull(5)?null:DateOnly.Parse(r.GetString(5)))); return x; }
    public List<WatchedEpisode> GetEpisodes(SqliteConnection c) { using var q=c.CreateCommand(); q.CommandText="SELECT * FROM WatchedEpisode"; using var r=q.ExecuteReader(); var x=new List<WatchedEpisode>(); while(r.Read()) x.Add(new(Guid.Parse(r.GetString(0)),r.IsDBNull(1)?null:Guid.Parse(r.GetString(1)),r.GetString(2),NInt(r,3),NInt(r,4),r.IsDBNull(5)?null:r.GetString(5),r.IsDBNull(6)?null:DateOnly.Parse(r.GetString(6)))); return x; }
    public ImportRun? GetLatestImportRun(SqliteConnection c)
    {
        using var q=c.CreateCommand(); q.CommandText="SELECT Id,ImportedAt,SourceFilename,FileHash,ImportedCount,UpdatedCount,SkippedCount,ErrorsAndWarnings FROM ImportRun ORDER BY ImportedAt DESC LIMIT 1";
        using var r=q.ExecuteReader(); if(!r.Read()) return null;
        return new(Guid.Parse(r.GetString(0)),DateTimeOffset.Parse(r.GetString(1)),r.GetString(2),r.GetString(3),r.GetInt32(4),r.GetInt32(5),r.GetInt32(6),r.GetString(7));
    }
    public List<(string Message,int Count)> GetUnresolvedWarnings(SqliteConnection c)
    {
        var issues=new List<(string,int)>();
        using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT MIN(display_title),SUM(n) FROM (SELECT upper(trim(Title)) title_key,MIN(Title) display_title,COUNT(*) n FROM Series GROUP BY upper(trim(Title)),EpisodeDurationMinutes HAVING COUNT(*)>1) GROUP BY title_key";
            using var r=q.ExecuteReader();while(r.Read())issues.Add(($"Сериал «{r.GetString(0)}»: одинаковые названия и длительность не позволяют сопоставить записи.",r.GetInt32(1)));
        }
        using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT MIN(display_title),SUM(n) FROM (SELECT upper(trim(Title)) title_key,MIN(Title) display_title,COUNT(*) n FROM Movie GROUP BY upper(trim(Title)),DurationMinutes HAVING COUNT(*)>1) GROUP BY title_key";
            using var r=q.ExecuteReader();while(r.Read())issues.Add(($"Фильм «{r.GetString(0)}»: одинаковые названия и длительность не позволяют сопоставить записи.",r.GetInt32(1)));
        }
        using(var q=c.CreateCommand())
        {
            q.CommandText="SELECT SeriesTitleFromSource,COUNT(*) FROM WatchedEpisode WHERE SeriesId IS NULL GROUP BY SeriesTitleFromSource ORDER BY SeriesTitleFromSource";
            using var r=q.ExecuteReader();while(r.Read())issues.Add(($"Эпизоды «{r.GetString(0)}» не связаны с сериалом.",r.GetInt32(1)));
        }
        return issues;
    }
    internal static object Db(object? x) => x ?? DBNull.Value;
    private static decimal? NDec(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetDecimal(i);
    private static int? NInt(SqliteDataReader r,int i)=>r.IsDBNull(i)?null:r.GetInt32(i);
}
