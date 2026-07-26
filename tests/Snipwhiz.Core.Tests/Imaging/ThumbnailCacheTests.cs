using System.Drawing;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Storage;
using Xunit;

namespace Snipwhiz.Core.Tests.Imaging;

public class ThumbnailCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "snipwhiz-tests", Guid.NewGuid().ToString("N"));
    private readonly CaptureStore _store;

    public ThumbnailCacheTests() => _store = new CaptureStore(_root);

    private static CroppedImage Image(int w, int h)
    {
        var bgra = new byte[w * h * 4];
        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var o = (y * w + x) * 4;
            bgra[o + 0] = (byte)(x % 256);
            bgra[o + 1] = (byte)(y % 256);
            bgra[o + 2] = 128;
            bgra[o + 3] = 255;
        }
        return new CroppedImage(bgra, w, h, false);
    }

    private CaptureRecord Save(int w, int h) => _store.Save(Image(w, h), "app", "title");

    private static (int Width, int Height) SizeOf(string path)
    {
        using var image = System.Drawing.Image.FromFile(path);
        return (image.Width, image.Height);
    }

    private static bool IsJpeg(string path)
    {
        using var stream = File.OpenRead(path);
        return stream.ReadByte() == 0xFF && stream.ReadByte() == 0xD8;
    }

    [Fact]
    public async Task First_call_writes_a_jpeg_under_thumbs()
    {
        var cache = new ThumbnailCache(_store);
        var record = Save(800, 400);

        var path = await cache.GetOrCreateAsync(record, CancellationToken.None);

        Assert.True(File.Exists(path));
        Assert.True(IsJpeg(path));
        Assert.Equal(Path.Combine(_root, "thumbs"), Path.GetDirectoryName(path));
    }

    [Fact]
    public async Task A_wide_capture_is_scaled_to_a_320_pixel_long_edge()
    {
        var cache = new ThumbnailCache(_store);

        var path = await cache.GetOrCreateAsync(Save(800, 400), CancellationToken.None);

        Assert.Equal((320, 160), SizeOf(path));
    }

    [Fact]
    public async Task A_tall_capture_is_scaled_on_its_own_long_edge()
    {
        var cache = new ThumbnailCache(_store);

        var path = await cache.GetOrCreateAsync(Save(400, 800), CancellationToken.None);

        Assert.Equal((160, 320), SizeOf(path));
    }

    [Fact]
    public async Task A_capture_smaller_than_the_thumbnail_size_is_not_upscaled()
    {
        var cache = new ThumbnailCache(_store);

        var path = await cache.GetOrCreateAsync(Save(100, 50), CancellationToken.None);

        Assert.Equal((100, 50), SizeOf(path));
    }

    [Fact]
    public async Task A_second_call_reuses_the_cached_file()
    {
        var cache = new ThumbnailCache(_store);
        var record = Save(800, 400);

        var path = await cache.GetOrCreateAsync(record, CancellationToken.None);
        var stamp = File.GetLastWriteTimeUtc(path);

        await Task.Delay(20);
        var again = await cache.GetOrCreateAsync(record, CancellationToken.None);

        Assert.Equal(path, again);
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(again));
    }

    [Fact]
    public async Task A_corrupt_cached_thumbnail_is_regenerated()
    {
        var cache = new ThumbnailCache(_store);
        var record = Save(800, 400);
        var path = await cache.GetOrCreateAsync(record, CancellationToken.None);

        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03]);

        var again = await cache.GetOrCreateAsync(record, CancellationToken.None);

        Assert.True(IsJpeg(again));
        Assert.Equal((320, 160), SizeOf(again));
    }

    [Fact]
    public async Task A_cancelled_request_throws_before_writing_anything()
    {
        // Honest about its reach: an already-cancelled token trips the pre-flight
        // check, so this proves the early exit and nothing about a cancellation
        // that lands mid-encode. The temp-then-move discipline is what covers
        // that, and the assertion below is what observes it.
        var cache = new ThumbnailCache(_store);
        var record = Save(800, 400);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => cache.GetOrCreateAsync(record, cts.Token));

        var thumbs = Path.Combine(_root, "thumbs");
        Assert.True(!Directory.Exists(thumbs) || Directory.GetFiles(thumbs).Length == 0);
    }

    [Fact]
    public async Task Generation_leaves_no_temporary_file_behind()
    {
        var cache = new ThumbnailCache(_store);

        await cache.GetOrCreateAsync(Save(800, 400), CancellationToken.None);

        var leftovers = Directory.GetFiles(Path.Combine(_root, "thumbs"), "*.tmp");
        Assert.Empty(leftovers);
    }

    [Fact]
    public async Task A_missing_original_surfaces_as_a_decode_failure()
    {
        var cache = new ThumbnailCache(_store);
        var record = Save(800, 400);
        File.Delete(_store.ResolvePath(record));

        await Assert.ThrowsAsync<ImageDecodeException>(
            () => cache.GetOrCreateAsync(record, CancellationToken.None));

        var thumbs = Path.Combine(_root, "thumbs");
        Assert.True(!Directory.Exists(thumbs) || Directory.GetFiles(thumbs).Length == 0);
    }

    [Fact]
    public async Task Concurrent_requests_for_the_same_capture_all_succeed()
    {
        var cache = new ThumbnailCache(_store);
        var record = Save(800, 400);

        var paths = await Task.WhenAll(Enumerable.Range(0, 8)
            .Select(_ => cache.GetOrCreateAsync(record, CancellationToken.None)));

        Assert.All(paths, p => Assert.True(IsJpeg(p)));
        Assert.Single(paths.Distinct());
    }

    public void Dispose()
    {
        _store.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch (IOException) { }
    }
}
