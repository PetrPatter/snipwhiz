using Microsoft.Data.Sqlite;

namespace Snipwhiz.Core.Storage;

public sealed class LibraryDb : IDisposable
{
    private const int CurrentSchemaVersion = 2;
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

    /// <summary>
    /// Stepwise, one block per version, stamped once at the end inside the
    /// transaction. The earlier single-script form hard-coded
    /// <c>user_version = 1</c>, so raising <see cref="CurrentSchemaVersion"/>
    /// left every database — including brand new ones — stamped 1 and
    /// re-migrating on every open.
    /// </summary>
    private void Migrate()
    {
        using (var wal = _connection.CreateCommand())
        {
            wal.CommandText = "PRAGMA journal_mode=WAL;";
            wal.ExecuteNonQuery();
        }

        var version = SchemaVersion;
        if (version >= CurrentSchemaVersion) return;

        using var tx = _connection.BeginTransaction();
        using var cmd = _connection.CreateCommand();
        cmd.Transaction = tx;

        if (version < 1)
        {
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
                """;
            cmd.ExecuteNonQuery();
        }

        if (version < 2)
        {
            cmd.CommandText = """
                CREATE INDEX IF NOT EXISTS idx_captures_created
                    ON captures(created_utc DESC, id DESC);
                """;
            cmd.ExecuteNonQuery();
        }

        // PRAGMA takes a literal, not a parameter. CurrentSchemaVersion is a
        // compile-time constant, so there is no injection surface here.
        cmd.CommandText = $"PRAGMA user_version = {CurrentSchemaVersion};";
        cmd.ExecuteNonQuery();

        tx.Commit();
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

    private const string SelectColumns =
        "SELECT id, created_utc, width, height, source_app, source_title, file_path FROM captures";

    public IReadOnlyList<CaptureRecord> Recent(int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"{SelectColumns} ORDER BY created_utc DESC, id DESC LIMIT $limit;";
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadAll(cmd);
    }

    /// <summary>
    /// One page, newest first. Keyset rather than OFFSET: a capture taken between
    /// two page fetches shifts every offset by one, so the last row of a page
    /// reappears as the first row of the next. Live insert makes that routine.
    /// </summary>
    /// <param name="after">The last record of the previous page; null for the first.</param>
    public IReadOnlyList<CaptureRecord> Page(CaptureRecord? after, int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            {SelectColumns}
            WHERE $first = 1
               OR created_utc < $created
               OR (created_utc = $created AND id < $id)
            ORDER BY created_utc DESC, id DESC
            LIMIT $limit;
            """;
        cmd.Parameters.AddWithValue("$first", after is null ? 1 : 0);
        cmd.Parameters.AddWithValue("$created", after?.CreatedUtc.ToUnixTimeMilliseconds() ?? 0L);
        // v7 GUIDs in "D" form are fixed-width lowercase hex, so text comparison
        // matches numeric order — and since v7 leads with the timestamp, the
        // tiebreak within a single millisecond is still time-ordered.
        cmd.Parameters.AddWithValue("$id", after?.Id.ToString("D") ?? "");
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadAll(cmd);
    }

    /// <summary>Substring match over source app and window title.</summary>
    public IReadOnlyList<CaptureRecord> Search(string query, int limit)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = $"""
            {SelectColumns}
            WHERE source_app   LIKE $q ESCAPE '\'
               OR source_title LIKE $q ESCAPE '\'
            ORDER BY created_utc DESC, id DESC
            LIMIT $limit;
            """;
        // Backslash first, or the escapes get escaped. Without this a typed '%'
        // matches every row in the library.
        var escaped = query
            .Replace("\\", "\\\\")
            .Replace("%", "\\%")
            .Replace("_", "\\_");
        cmd.Parameters.AddWithValue("$q", $"%{escaped}%");
        cmd.Parameters.AddWithValue("$limit", limit);
        return ReadAll(cmd);
    }

    /// <returns>True if a row was removed.</returns>
    public bool Delete(Guid id)
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "DELETE FROM captures WHERE id = $id;";
        cmd.Parameters.AddWithValue("$id", id.ToString("D"));
        return cmd.ExecuteNonQuery() > 0;
    }

    public int Count()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM captures;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    private static List<CaptureRecord> ReadAll(SqliteCommand cmd)
    {
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
