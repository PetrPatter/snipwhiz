using Snipwhiz.Core.Storage;
using Xunit;

namespace Snipwhiz.Core.Tests.Storage;

public class LibraryQueryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snipwhiz-tests", Guid.NewGuid().ToString("N"));

    public LibraryQueryTests() => Directory.CreateDirectory(_dir);

    private LibraryDb OpenDb() => new(Path.Combine(_dir, "library.db"));

    /// <summary>
    /// Walks every page and returns the ids in the order they arrived.
    /// </summary>
    /// <param name="expected">
    /// Row count the caller seeded. Used only to bound the walk: a pager that
    /// ignores its cursor hands back the same newest page forever, and an
    /// unbounded loop would hang the suite instead of failing it. Observed
    /// happening — the first run of the keyset negative control never
    /// terminated.
    /// </param>
    private static List<Guid> PageThrough(LibraryDb db, int limit, int expected)
    {
        var seen = new List<Guid>();
        var maxPages = expected / limit + 2;
        CaptureRecord? after = null;

        for (var page = 0; page <= maxPages; page++)
        {
            Assert.True(page < maxPages,
                $"Pager did not terminate within {maxPages} pages — it is not honouring its cursor.");

            var rows = db.Page(after, limit);
            if (rows.Count == 0) break;
            seen.AddRange(rows.Select(r => r.Id));
            after = rows[^1];
        }
        return seen;
    }

    [Fact]
    public void Paging_returns_every_row_exactly_once_newest_first()
    {
        using var db = OpenDb();
        var seeded = LibrarySeeder.Seed(db, 250);

        var seen = PageThrough(db, 100, 250);

        Assert.Equal(250, seen.Count);
        Assert.Equal(250, seen.Distinct().Count());
        // Seeded oldest-first, so newest-first is the reverse of insertion order.
        Assert.Equal(seeded.Select(r => r.Id).Reverse(), seen);
    }

    [Fact]
    public void A_row_inserted_between_pages_does_not_duplicate_an_earlier_row()
    {
        // This is the bug OFFSET paging has and keyset does not: inserting at the
        // top shifts every offset by one, so the last row of page 1 reappears as
        // the first row of page 2. Run this against an OFFSET implementation and
        // it fails.
        using var db = OpenDb();
        LibrarySeeder.Seed(db, 250);

        var first = db.Page(null, 100);
        Assert.Equal(100, first.Count);

        // Newer than everything seeded.
        LibrarySeeder.Seed(db, 1, start: LibrarySeeder.Base.AddDays(1));

        var second = db.Page(first[^1], 100);

        Assert.Empty(second.Select(r => r.Id).Intersect(first.Select(r => r.Id)));
    }

    [Fact]
    public void Rows_sharing_a_timestamp_are_paged_without_loss_or_repetition()
    {
        using var db = OpenDb();
        LibrarySeeder.SeedSameInstant(db, 5);

        var seen = PageThrough(db, 2, 5);

        Assert.Equal(5, seen.Count);
        Assert.Equal(5, seen.Distinct().Count());
    }

    [Fact]
    public void Search_matches_app_and_title_case_insensitively()
    {
        using var db = OpenDb();
        LibrarySeeder.Seed(db, 2, describe: i => i == 0
            ? ("chrome", "Google - Chrome")
            : ("code", "Program.cs"));

        Assert.Single(db.Search("CHR", 50));
        Assert.Single(db.Search("program", 50));
        Assert.Equal(2, db.Search("", 50).Count);
    }

    [Fact]
    public void Search_treats_percent_as_a_literal_not_a_wildcard()
    {
        // An unescaped LIKE '%%%' matches everything, so this returns 2 and fails.
        using var db = OpenDb();
        LibrarySeeder.Seed(db, 2, describe: i => i == 0
            ? ("app", "100% done")
            : ("app", "abc"));

        var hits = db.Search("%", 50);

        Assert.Single(hits);
        Assert.Equal("100% done", hits[0].SourceTitle);
    }

    [Fact]
    public void Search_treats_underscore_as_a_literal_not_a_single_character_wildcard()
    {
        using var db = OpenDb();
        LibrarySeeder.Seed(db, 2, describe: i => i == 0
            ? ("app", "my_file")
            : ("app", "myXfile"));

        var hits = db.Search("y_f", 50);

        Assert.Single(hits);
        Assert.Equal("my_file", hits[0].SourceTitle);
    }

    [Fact]
    public void Delete_removes_the_row_and_reports_whether_it_existed()
    {
        using var db = OpenDb();
        var seeded = LibrarySeeder.Seed(db, 3);

        Assert.True(db.Delete(seeded[1].Id));
        Assert.Equal(2, db.Count());
        Assert.DoesNotContain(db.Page(null, 50), r => r.Id == seeded[1].Id);

        Assert.False(db.Delete(seeded[1].Id));
    }

    [Fact]
    public void SetEditPaths_records_the_result_of_a_save()
    {
        using var db = OpenDb();
        var seeded = LibrarySeeder.Seed(db, 1);
        var edited = new DateTimeOffset(2026, 7, 27, 9, 30, 0, TimeSpan.Zero);

        db.SetEditPaths(seeded[0].Id, "projects/x.ssproj", "flat/x.png", 220, 130, edited);

        var row = db.Page(null, 10).Single();
        Assert.Equal("projects/x.ssproj", row.ProjectPath);
        Assert.Equal("flat/x.png", row.FlatPath);
        Assert.Equal(220, row.FlatWidth);
        Assert.Equal(130, row.FlatHeight);
        Assert.Equal(edited, row.EditedUtc);
    }

    [Fact]
    public void A_failed_flatten_leaves_the_project_recorded_and_no_render()
    {
        // Spec 2b §4.12: the project is still committed when the render fails, so
        // the capture keeps its annotations and the library falls back to the
        // original. A NOT NULL on flat_path would break exactly this.
        using var db = OpenDb();
        var seeded = LibrarySeeder.Seed(db, 1);

        db.SetEditPaths(seeded[0].Id, "projects/x.ssproj", null, null, null, DateTimeOffset.UnixEpoch);

        var row = db.Page(null, 10).Single();
        Assert.Equal("projects/x.ssproj", row.ProjectPath);
        Assert.Null(row.FlatPath);
    }

    [Fact]
    public void Deleting_an_edited_capture_and_undoing_keeps_it_joined_to_its_annotations()
    {
        // Undo-of-delete re-inserts a record that came back out of this table. A
        // column dropped by Insert severs the capture from its .ssproj silently,
        // and nothing surfaces it until someone opens the editor and finds their
        // work gone.
        using var db = OpenDb();
        var seeded = LibrarySeeder.Seed(db, 1);
        db.SetEditPaths(seeded[0].Id, "projects/x.ssproj", "flat/x.png", 220, 130,
                        DateTimeOffset.FromUnixTimeMilliseconds(1_753_600_000_000));

        var before = db.Page(null, 10).Single();
        db.Delete(before.Id);
        db.Insert(before);                       // the undo path

        Assert.Equal(before, db.Page(null, 10).Single());
    }

    [Fact]
    public void Editing_a_capture_does_not_move_it_in_the_library()
    {
        // Spec 2b §5: edited_utc is stored but is not an ordering key. The library
        // is a record of when captures were taken.
        using var db = OpenDb();
        var seeded = LibrarySeeder.Seed(db, 3);
        var middle = seeded[1];

        db.SetEditPaths(middle.Id, "projects/x.ssproj", "flat/x.png", 10, 10, DateTimeOffset.UtcNow);

        // Seeded oldest-first, so newest-first is the reverse of insertion order.
        Assert.Equal(seeded.Select(r => r.Id).Reverse(), db.Page(null, 10).Select(r => r.Id));
    }

    [Fact]
    public void Count_matches_the_number_of_rows()
    {
        using var db = OpenDb();
        Assert.Equal(0, db.Count());
        LibrarySeeder.Seed(db, 17);
        Assert.Equal(17, db.Count());
    }

    [Fact]
    public void TotalBytes_sums_the_capture_files_and_ignores_the_database()
    {
        using var store = new CaptureStore(_dir);
        var a = store.Save(LibrarySeeder.Image(), "app", "t");
        var b = store.Save(LibrarySeeder.Image(16, 8), "app", "t");

        var expected = new FileInfo(store.ResolvePath(a)).Length
                     + new FileInfo(store.ResolvePath(b)).Length;

        Assert.Equal(expected, store.TotalBytes());
        // library.db exists and is non-trivial in size; if it were counted the
        // assertion above could not hold.
        Assert.True(new FileInfo(Path.Combine(_dir, "library.db")).Length > 0);
    }

    [Fact]
    public void TotalBytes_is_zero_before_anything_is_captured()
    {
        using var store = new CaptureStore(_dir);
        Assert.Equal(0, store.TotalBytes());
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
