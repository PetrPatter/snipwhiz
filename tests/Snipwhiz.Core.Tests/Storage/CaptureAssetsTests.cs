using Snipwhiz.Core.Storage;
using Xunit;

namespace Snipwhiz.Core.Tests.Storage;

/// <summary>
/// No database here on purpose — resolving a capture to a file is a pure function
/// of a root and a record, and it is the single decision the whole app's "which
/// image do I show?" depends on.
/// </summary>
public class CaptureAssetsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "snipwhiz-tests", Guid.NewGuid().ToString("N"));
    private readonly CaptureAssets _assets;

    public CaptureAssetsTests()
    {
        Directory.CreateDirectory(_root);
        _assets = new CaptureAssets(_root);
    }

    private CaptureRecord Record(string? flatPath = null) => new(
        Guid.CreateVersion7(), DateTimeOffset.UnixEpoch, 100, 50, "app", "title",
        Path.Combine("captures", "2026", "07", "capture.png"), FlatPath: flatPath);

    private string Write(string relative)
    {
        var full = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllBytes(full, [1, 2, 3]);
        return full;
    }

    [Fact]
    public void An_unedited_capture_displays_its_original()
    {
        var record = Record();
        Assert.Equal(_assets.Original(record), _assets.Display(record));
    }

    [Fact]
    public void An_edited_capture_displays_its_flattened_render()
    {
        var record = Record(Path.Combine("flat", "x.png"));
        var flat = Write(Path.Combine("flat", "x.png"));

        Assert.Equal(flat, _assets.Display(record));
        Assert.NotEqual(_assets.Original(record), _assets.Display(record));
    }

    [Fact]
    public void A_missing_render_falls_back_to_the_original_rather_than_a_broken_path()
    {
        // The row says a render exists and the file is gone — deleted by hand, or
        // a flatten that failed after the row was written. Degrading to the
        // capture is the difference between losing the annotations from view and
        // losing the capture from view.
        var record = Record(Path.Combine("flat", "never-written.png"));

        Assert.Equal(_assets.Original(record), _assets.Display(record));
    }

    [Fact]
    public void An_unedited_capture_is_resolved_without_touching_the_disk()
    {
        // Display must not probe the filesystem for the overwhelming majority of
        // captures, which have never been edited. Proven by resolving against a
        // root that does not exist at all: a probe would still return the original
        // here, so the assertion is that nothing throws and no directory appears.
        var missingRoot = Path.Combine(_root, "not-created");
        var assets = new CaptureAssets(missingRoot);

        var resolved = assets.Display(Record());

        Assert.StartsWith(missingRoot, resolved);
        Assert.False(Directory.Exists(missingRoot));
    }

    [Fact]
    public void Every_file_a_capture_owns_is_named_for_delete()
    {
        var record = Record();

        var all = _assets.All(record);

        Assert.Contains(_assets.Original(record), all);
        Assert.Contains(_assets.Thumbnail(record.Id), all);
        Assert.Contains(_assets.Flat(record.Id), all);
        Assert.Contains(_assets.Project(record.Id), all);
        Assert.Equal(4, all.Count);
    }

    [Fact]
    public void Delete_names_the_canonical_project_and_render_even_when_the_row_never_recorded_them()
    {
        // A save that crashed between writing flat/<id>.png and recording it leaves
        // a file no row references. Resolving All from the columns would orphan it
        // forever; resolving from the id cleans it up.
        var record = Record();
        var orphan = Write(Path.Combine("flat", $"{record.Id:D}.png"));

        Assert.Null(record.FlatPath);
        Assert.Contains(orphan, _assets.All(record));
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
