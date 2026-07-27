using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Storage;
using Xunit;

namespace Snipwhiz.Core.Tests.Storage;

public class CaptureStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "snipwhiz-tests", Guid.NewGuid().ToString("N"));

    private static CroppedImage Image(int w = 8, int h = 4)
    {
        var bgra = new byte[w * h * 4];
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return new CroppedImage(bgra, w, h, false);
    }

    [Fact]
    public void Save_writes_a_png_and_returns_a_record_pointing_at_it()
    {
        using var store = new CaptureStore(_root);
        var record = store.Save(Image(), "chrome", "Northwind Analytics");

        var full = Path.Combine(_root, record.FilePath);
        Assert.True(File.Exists(full));
        Assert.Equal(8, record.Width);
        Assert.Equal(4, record.Height);
        Assert.Equal("chrome", record.SourceApp);
        Assert.Equal("Northwind Analytics", record.SourceTitle);
    }

    [Fact]
    public void Save_buckets_files_by_year_and_month()
    {
        using var store = new CaptureStore(_root);
        var record = store.Save(Image(), "app", "title");

        var now = DateTimeOffset.UtcNow;
        Assert.StartsWith(Path.Combine("captures", now.ToString("yyyy"), now.ToString("MM")), record.FilePath);
    }

    [Fact]
    public void Saved_records_come_back_newest_first()
    {
        using var store = new CaptureStore(_root);
        var a = store.Save(Image(), "a", "first");
        var b = store.Save(Image(), "b", "second");
        var c = store.Save(Image(), "c", "third");

        var recent = store.Recent(10);

        Assert.Equal(new[] { c.Id, b.Id, a.Id }, recent.Select(r => r.Id));
    }

    [Fact]
    public void Ids_are_time_ordered()
    {
        using var store = new CaptureStore(_root);
        var ids = Enumerable.Range(0, 20).Select(_ => store.Save(Image(), "a", "t").Id).ToList();
        Assert.Equal(ids.OrderBy(i => i).ToList(), ids);   // UUIDv7 sorts by creation time
    }

    [Fact]
    public void Reopening_the_store_sees_earlier_captures()
    {
        Guid id;
        using (var first = new CaptureStore(_root)) id = first.Save(Image(), "a", "t").Id;
        using var second = new CaptureStore(_root);
        Assert.Contains(second.Recent(10), r => r.Id == id);
    }

    [Fact]
    public void Schema_version_is_stamped()
    {
        using var store = new CaptureStore(_root);
        store.Save(Image(), "a", "t");
        Assert.Equal(2, store.SchemaVersion);
    }

    [Fact]
    public void Save_refuses_to_overwrite_an_existing_capture()
    {
        // A fixed id forces the second Save onto the first one's exact path.
        var fixedId = Guid.CreateVersion7();
        using var store = new CaptureStore(_root, () => fixedId);

        var first = store.Save(Image(), "app", "first");
        var firstBytes = File.ReadAllBytes(Path.Combine(_root, first.FilePath));

        Assert.Throws<IOException>(() => store.Save(Image(16, 8), "app", "second"));

        // The original capture is byte-for-byte intact.
        Assert.Equal(firstBytes, File.ReadAllBytes(Path.Combine(_root, first.FilePath)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
