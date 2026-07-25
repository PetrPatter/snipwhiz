using Snipwhiz.Core;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Clipboard;
using Snipwhiz.Core.Geometry;
using Snipwhiz.Core.Storage;
using Xunit;

namespace Snipwhiz.Core.Tests;

public class CapturePipelineTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "snipwhiz-pipeline", Guid.NewGuid().ToString("N"));

    /// <param name="fill">Applied to R, G and B of every pixel.</param>
    private static FrozenDesktop Frozen(byte fill = 200, PixelRect? monitor = null)
    {
        var bounds = monitor ?? new PixelRect(0, 0, 40, 20);
        var desktop = VirtualDesktop.FromMonitors(new[] { new MonitorInfo("A", bounds, 1.0, true) });
        var bgra = new byte[bounds.Width * bounds.Height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = bgra[i + 1] = bgra[i + 2] = fill;
            bgra[i + 3] = 255;
        }
        return new FrozenDesktop(desktop, bgra, CursorState.None);
    }

    [Fact]
    public void Clipboard_is_written_before_disk()
    {
        var order = new List<string>();

        // CaptureStore.Save calls newId() as its first statement, so this records
        // the moment the disk write begins — no extra seam needed.
        using var store = new CaptureStore(_root, () =>
        {
            order.Add("disk");
            return Guid.CreateVersion7();
        });
        var pipeline = new CapturePipeline(store, _ => order.Add("clipboard"));

        var outcome = pipeline.Complete(Frozen(), new PixelRect(0, 0, 10, 10), "app", "title");

        // Strict sequence: reversing the two calls in Complete must fail this.
        Assert.Equal(new[] { "clipboard", "disk" }, order);
        Assert.True(outcome.ClipboardOk);
        Assert.True(outcome.SaveOk);
    }

    [Fact]
    public void A_failing_clipboard_still_saves_to_disk()
    {
        using var store = new CaptureStore(_root);
        var pipeline = new CapturePipeline(store,
            _ => throw new ClipboardUnavailableException("held by another app"));

        var outcome = pipeline.Complete(Frozen(), new PixelRect(0, 0, 10, 10), "app", "title");

        Assert.False(outcome.ClipboardOk);
        Assert.True(outcome.SaveOk);
        Assert.NotNull(outcome.Record);
        Assert.Contains("clipboard", outcome.Warning!, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void An_all_black_capture_explains_that_DRM_cannot_be_captured()
    {
        using var store = new CaptureStore(_root);
        var pipeline = new CapturePipeline(store, _ => { });

        var outcome = pipeline.Complete(Frozen(fill: 0), new PixelRect(0, 0, 10, 10), "app", "title");

        Assert.Contains("DRM", outcome.Warning!);
    }

    [Fact]
    public void An_uncovered_region_is_reported_and_takes_priority_over_the_black_warning()
    {
        // Two displays, second offset up, leaving a gap bottom-right.
        var desktop = VirtualDesktop.FromMonitors(new[]
        {
            new MonitorInfo("A", new PixelRect(0, 0, 40, 40), 1.0, true),
            new MonitorInfo("B", new PixelRect(40, 0, 40, 20), 1.0, false),
        });
        var bgra = new byte[80 * 40 * 4];
        var frozen = new FrozenDesktop(desktop, bgra, CursorState.None);

        using var store = new CaptureStore(_root);
        var pipeline = new CapturePipeline(store, _ => { });

        var outcome = pipeline.Complete(frozen, new PixelRect(50, 25, 20, 10), "app", "title");

        Assert.True(outcome.HasUncoveredPixels);
        Assert.Contains("not covered by any display", outcome.Warning!);
        Assert.DoesNotContain("DRM", outcome.Warning!);
    }

    [Fact]
    public void A_failing_database_insert_reports_the_disk_write_message_and_leaves_no_orphan_png()
    {
        // Save writes the PNG first and inserts the row second, so a full disk or a
        // locked DB surfaces as SqliteException from the insert — which used to fall
        // straight through the handler as a generic failure and strand the PNG.
        var id = Guid.CreateVersion7();
        using var store = new CaptureStore(_root, () => id);
        var pipeline = new CapturePipeline(store, _ => { });

        var first = pipeline.Complete(Frozen(), new PixelRect(0, 0, 10, 10), "app", "title");
        var path = Path.Combine(_root, first.Record!.FilePath);

        // Free the filename so the *insert* (duplicate primary key) is what fails,
        // rather than the FileMode.CreateNew write.
        File.Delete(path);

        var outcome = pipeline.Complete(Frozen(), new PixelRect(0, 0, 10, 10), "app", "title");

        Assert.False(outcome.SaveOk);
        Assert.Null(outcome.Record);
        Assert.Contains("saving to disk failed", outcome.Warning!);
        Assert.False(File.Exists(path));   // the orphan PNG was cleaned up
    }

    [Fact]
    public void A_normal_capture_produces_no_warning()
    {
        using var store = new CaptureStore(_root);
        var pipeline = new CapturePipeline(store, _ => { });

        var outcome = pipeline.Complete(Frozen(), new PixelRect(5, 5, 10, 10), "chrome", "Northwind");

        Assert.Null(outcome.Warning);
        Assert.Equal(10, outcome.Record!.Width);
        Assert.Equal("chrome", outcome.Record.SourceApp);
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
