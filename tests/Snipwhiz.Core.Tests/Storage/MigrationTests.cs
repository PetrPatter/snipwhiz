using Microsoft.Data.Sqlite;
using Snipwhiz.Core.Storage;
using Xunit;

namespace Snipwhiz.Core.Tests.Storage;

public class MigrationTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snipwhiz-tests", Guid.NewGuid().ToString("N"));

    private string DbPath => Path.Combine(_dir, "library.db");

    public MigrationTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// Builds a database shaped exactly like the one spec 1 shipped: the v1 table
    /// and PRAGMA user_version = 1, with no index. This is the only fixture that
    /// exercises the upgrade path a real user's library will take.
    /// </summary>
    private void WriteV1Database(params (Guid Id, long Created)[] rows)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            Pooling = false,
        }.ToString());
        connection.Open();

        using (var cmd = connection.CreateCommand())
        {
            cmd.CommandText = """
                CREATE TABLE captures (
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

        foreach (var (id, created) in rows)
        {
            using var insert = connection.CreateCommand();
            insert.CommandText = """
                INSERT INTO captures (id, created_utc, width, height, source_app, source_title, file_path)
                VALUES ($id, $created, 100, 50, 'legacy', 'Legacy Window', 'captures/2026/07/x.png');
                """;
            insert.Parameters.AddWithValue("$id", id.ToString("D"));
            insert.Parameters.AddWithValue("$created", created);
            insert.ExecuteNonQuery();
        }
    }

    /// <summary>
    /// Builds a database shaped exactly like the one spec 2a shipped: the v1 table,
    /// the ordering index, and PRAGMA user_version = 2. This is the upgrade path a
    /// library that has been in use since spec 2a will actually take.
    /// </summary>
    private void WriteV2Database(params (Guid Id, long Created)[] rows)
    {
        WriteV1Database(rows);

        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false,
        }.ToString());
        connection.Open();

        using var cmd = connection.CreateCommand();
        cmd.CommandText = """
            CREATE INDEX IF NOT EXISTS idx_captures_created
                ON captures(created_utc DESC, id DESC);
            PRAGMA user_version = 2;
            """;
        cmd.ExecuteNonQuery();
    }

    private int CountIndexes()
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = DbPath,
            Mode = SqliteOpenMode.ReadOnly,
            Pooling = false,
        }.ToString());
        connection.Open();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name='idx_captures_created';";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    [Fact]
    public void A_fresh_database_reports_the_current_schema_version()
    {
        using var db = new LibraryDb(DbPath);
        Assert.Equal(3, db.SchemaVersion);
    }

    [Fact]
    public void A_fresh_database_has_the_ordering_index()
    {
        using (var db = new LibraryDb(DbPath)) { }
        Assert.Equal(1, CountIndexes());
    }

    /// <summary>
    /// Two versions in one open. This is the path for a library that has not been
    /// touched since spec 1 — it must not need an intermediate run of spec 2a.
    /// </summary>
    [Fact]
    public void A_v1_database_upgrades_in_place_and_keeps_its_rows()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        WriteV1Database((first, 1_000), (second, 2_000));

        using var db = new LibraryDb(DbPath);

        Assert.Equal(3, db.SchemaVersion);
        Assert.Equal(1, CountIndexes());

        var rows = db.Recent(10);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Id == first);
        Assert.Contains(rows, r => r.Id == second);
    }

    [Fact]
    public void A_v2_database_upgrades_to_v3_and_keeps_its_rows()
    {
        var first = Guid.CreateVersion7();
        var second = Guid.CreateVersion7();
        WriteV2Database((first, 1_000), (second, 2_000));

        using var db = new LibraryDb(DbPath);

        Assert.Equal(3, db.SchemaVersion);
        Assert.Equal(1, CountIndexes());

        var rows = db.Recent(10);
        Assert.Equal(2, rows.Count);
        Assert.Contains(rows, r => r.Id == first);
        Assert.Contains(rows, r => r.Id == second);

        // Every capture that predates the editor has no project and no render.
        Assert.All(rows, r =>
        {
            Assert.Null(r.ProjectPath);
            Assert.Null(r.FlatPath);
            Assert.Null(r.FlatWidth);
            Assert.Null(r.FlatHeight);
            Assert.Null(r.EditedUtc);
        });
    }

    [Fact]
    public void Migrating_twice_leaves_one_index_and_the_current_version()
    {
        using (var db = new LibraryDb(DbPath)) { }
        using (var db = new LibraryDb(DbPath)) { Assert.Equal(3, db.SchemaVersion); }

        Assert.Equal(1, CountIndexes());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
