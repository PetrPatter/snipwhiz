using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.Core.Imaging;

/// <summary>
/// Lazily produced, disk-cached grid previews at <see cref="LongEdge"/> pixels.
///
/// Not generated at capture time: the capture path has roughly 40 ms of headroom
/// left in its 120 ms budget (spec 1 §4.5) and a PNG decode plus a JPEG encode
/// would spend all of it. Not generated on the fly either — decoding a 4K PNG per
/// tile cannot keep up with a scrolling grid.
///
/// The cache is disposable. Losing it costs time and nothing else, so every
/// failure path here prefers regenerating over reporting.
/// </summary>
public sealed class ThumbnailCache(CaptureStore store) : IDisposable
{
    public const int LongEdge = 320;
    private const long JpegQuality = 82;

    /// <summary>
    /// JPEG has no alpha, so transparent pixels have to land on something. Spec
    /// 2a captures are all opaque and this never fires; spec 2b's editor produces
    /// transparency, and without this those thumbnails would come back black.
    /// </summary>
    private static readonly Color Surface = Color.FromArgb(255, 0x1C, 0x1B, 0x1A);

    // Decode + rescale + encode is CPU- and allocation-heavy. Half the cores
    // keeps a fast scroll through a large library from starving the UI thread.
    private readonly SemaphoreSlim _slots = new(Math.Max(1, Environment.ProcessorCount / 2));

    private string ThumbsDir => Path.Combine(store.Root, "thumbs");

    public string PathFor(Guid id) => Path.Combine(ThumbsDir, $"{id:D}.jpg");

    /// <returns>Absolute path of the cached JPEG.</returns>
    /// <exception cref="ImageDecodeException">The original is missing or unreadable.</exception>
    public async Task<string> GetOrCreateAsync(CaptureRecord record, CancellationToken ct)
    {
        var thumbPath = PathFor(record.Id);
        if (IsUsable(thumbPath)) return thumbPath;

        ct.ThrowIfCancellationRequested();
        await _slots.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            // Another request may have produced it while this one queued.
            if (IsUsable(thumbPath)) return thumbPath;

            ct.ThrowIfCancellationRequested();
            var source = store.ResolvePath(record);
            await Task.Run(() => Generate(source, thumbPath, ct), ct).ConfigureAwait(false);
            return thumbPath;
        }
        finally
        {
            _slots.Release();
        }
    }

    /// <summary>Best-effort; a thumbnail that will not delete is not worth reporting.</summary>
    public void Remove(Guid id)
    {
        try { File.Delete(PathFor(id)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Two bytes, not a decode. A full validation per cache hit would defeat the
    /// point of caching; the SOI marker catches the cases that actually happen —
    /// truncation from a crash mid-write, and zero-length files.
    /// </summary>
    private static bool IsUsable(string path)
    {
        try
        {
            using var stream = File.OpenRead(path);
            return stream.ReadByte() == 0xFF && stream.ReadByte() == 0xD8;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static void Generate(string sourcePath, string thumbPath, CancellationToken ct)
    {
        var image = PngDecoder.Decode(sourcePath);
        ct.ThrowIfCancellationRequested();

        var (width, height) = TargetSize(image.Width, image.Height);

        Directory.CreateDirectory(Path.GetDirectoryName(thumbPath)!);
        // Write beside the target and move into place: a cancellation or crash
        // part-way must never leave a truncated file that later passes the SOI
        // check and renders as a torn thumbnail.
        //
        // The temp name is unique per attempt, not just per capture. Two tiles can
        // request the same thumbnail concurrently — routine when a recycled
        // container rebinds during a fast scroll — and a shared temp name has them
        // writing the same file at once, which GDI+ surfaces as an opaque
        // "generic error". The final move is atomic, so the duplicate work is
        // wasted but harmless; a colliding temp file is not.
        var tempPath = $"{thumbPath}.{Guid.NewGuid():N}.tmp";

        try
        {
            var handle = GCHandle.Alloc(image.Bgra, GCHandleType.Pinned);
            try
            {
                using var source = new Bitmap(image.Width, image.Height, image.Width * 4,
                                              PixelFormat.Format32bppArgb, handle.AddrOfPinnedObject());
                using var target = new Bitmap(width, height, PixelFormat.Format24bppRgb);
                using (var g = Graphics.FromImage(target))
                {
                    g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    g.Clear(Surface);
                    g.DrawImage(source, new Rectangle(0, 0, width, height));
                }

                ct.ThrowIfCancellationRequested();
                target.Save(tempPath, JpegCodec(), QualityAt(JpegQuality));
            }
            finally
            {
                handle.Free();
            }

            Publish(tempPath, thumbPath);
        }
        catch
        {
            try { File.Delete(tempPath); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// Moves the finished thumbnail into place, tolerating another thread having
    /// got there first.
    ///
    /// The unique temp name above stops two concurrent generators writing the same
    /// file, but they still finish by replacing the same destination — and on
    /// Windows the second <c>MoveFileEx</c> can fail with access denied while the
    /// first is in flight. This is not rare: it failed four runs in six on a
    /// developer machine, and it surfaces in the app as a tile that never renders.
    ///
    /// The comment on the temp name always claimed the duplicate work was "wasted
    /// but harmless". It was not harmless, because nothing implemented the
    /// harmless part. This does: if the destination is usable, the other thread
    /// won and the loser's work is simply redundant.
    /// </summary>
    private static void Publish(string tempPath, string thumbPath)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Move(tempPath, thumbPath, overwrite: true);
                return;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // The winner moved a complete temp file, so a destination that
                // passes the SOI check is whole rather than half-written.
                if (IsUsable(thumbPath))
                {
                    try { File.Delete(tempPath); } catch (IOException) { }
                    return;
                }

                // Deciding on a single probe is not enough. IsUsable opens the
                // file, so while the winner's replace is still in flight it hits a
                // sharing violation and reports unusable — a capture that is about
                // to be perfectly fine. Retrying cut the residual failure rate from
                // 1-in-8 to none in 40 runs.
                if (attempt >= 4) throw;
                Thread.Sleep(20);
            }
        }
    }

    /// <summary>Fits the long edge to <see cref="LongEdge"/>, never enlarging.</summary>
    private static (int Width, int Height) TargetSize(int width, int height)
    {
        var longest = Math.Max(width, height);
        if (longest <= LongEdge) return (width, height);

        var factor = (double)LongEdge / longest;
        return (Math.Max(1, (int)Math.Round(width * factor)),
                Math.Max(1, (int)Math.Round(height * factor)));
    }

    private static ImageCodecInfo JpegCodec() =>
        ImageCodecInfo.GetImageEncoders().First(c => c.FormatID == ImageFormat.Jpeg.Guid);

    private static EncoderParameters QualityAt(long quality)
    {
        var parameters = new EncoderParameters(1);
        parameters.Param[0] = new EncoderParameter(Encoder.Quality, quality);
        return parameters;
    }

    public void Dispose() => _slots.Dispose();
}
