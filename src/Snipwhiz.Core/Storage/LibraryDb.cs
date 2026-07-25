using Microsoft.Data.Sqlite;

namespace Snipwhiz.Core.Storage;

public sealed class LibraryDb : IDisposable
{
    private const int CurrentSchemaVersion = 1;
    private readonly SqliteConnection _connection;

    public LibraryDb(string dbPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = dbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        _connection.Open();
        Migrate();
    }

    public int SchemaVersion
    {
        get
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = "PRAGMA user_version;";
            return Convert.ToInt32(cmd.ExecuteScalar());
        }
    }

    private void Migrate()
    {
        using var wal = _connection.CreateCommand();
        wal.CommandText = "PRAGMA journal_mode=WAL;";
        wal.ExecuteNonQuery();

        if (SchemaVersion >= CurrentSchemaVersion) return;

        using var cmd = _connection.CreateCommand();
        // Spec 2 adds columns; ALTER TABLE ADD COLUMN is free in SQLite, so
        // nothing is pre-built for it here.
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS captures (
                id           TEXT    PRIMARY KEY,
                created_utc  INTEGER NOT NULL,
                width        INTEGER NOT NULL,
                height       INTEGER NOT NULL,
                source_app   TEXT    NOT NULL,
                source_title TEXT    NOT NULL,
                file_path    TEXT    NOT NULL
            );
            PRAGMA user_version = 1;
            """;
        cmd.ExecuteNonQuery();
    }

    public void Insert(CaptureRecord r)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO captures (id, created_utc, width, height, source_app, source_title, file_path)
            VALUES ($id, $created, $w, $h, $app, $title, $path);
            """;
        cmd.Parameters.AddWithValue("$id", r.Id.ToString("D"));
        cmd.Parameters.AddWithValue("$created", r.CreatedUtc.ToUnixTimeMilliseconds());
        cmd.Parameters.AddWithValue("$w", r.Width);
        cmd.Parameters.AddWithValue("$h", r.Height);
        cmd.Parameters.AddWithValue("$app", r.SourceApp);
        cmd.Parameters.AddWithValue("$title", r.SourceTitle);
        cmd.Parameters.AddWithValue("$path", r.FilePath);
        cmd.ExecuteNonQuery();
    }

    public IReadOnlyList<CaptureRecord> Recent(int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, created_utc, width, height, source_app, source_title, file_path
            FROM captures ORDER BY created_utc DESC, id DESC LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$limit", limit);

        var results = new List<CaptureRecord>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            results.Add(new CaptureRecord(
                Guid.Parse(reader.GetString(0)),
                DateTimeOffset.FromUnixTimeMilliseconds(reader.GetInt64(1)),
                reader.GetInt32(2),
                reader.GetInt32(3),
                reader.GetString(4),
                reader.GetString(5),
                reader.GetString(6)));
        }
        return results;
    }

    public void Dispose() => _connection.Dispose();
}
