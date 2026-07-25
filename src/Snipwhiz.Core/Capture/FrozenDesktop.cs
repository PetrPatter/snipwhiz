using Snipwhiz.Core.Geometry;

namespace Snipwhiz.Core.Capture;

/// <summary>
/// An immutable snapshot of the whole virtual desktop. Displayed pixels and
/// saved pixels both come from here, so they cannot disagree.
/// </summary>
public sealed class FrozenDesktop
{
    public VirtualDesktop Desktop { get; }
    public CursorState Cursor { get; }
    /// <summary>Top-down BGRA32. Stride is Width * 4.</summary>
    public byte[] Bgra { get; }

    public PixelRect Bounds => Desktop.Bounds;
    public int Width => Desktop.Bounds.Width;
    public int Height => Desktop.Bounds.Height;

    public FrozenDesktop(VirtualDesktop desktop, byte[] bgra, CursorState cursor)
    {
        var expected = (long)desktop.Bounds.Width * desktop.Bounds.Height * 4;
        if (bgra.LongLength != expected)
            throw new ArgumentException($"Expected {expected} bytes, got {bgra.LongLength}.", nameof(bgra));

        Desktop = desktop;
        Bgra = bgra;
        Cursor = cursor;
    }

    /// <summary>
    /// Crop in virtual-screen physical pixels. Because the grab is a single pass
    /// over the whole virtual desktop, this is a translation by the virtual
    /// origin — there is no per-monitor case.
    /// </summary>
    public CroppedImage Crop(PixelRect region)
    {
        var clamped = region.ClampTo(Bounds);
        if (clamped.IsEmpty)
            throw new ArgumentException($"Region {region} is empty or fully outside {Bounds}.", nameof(region));

        var srcX = clamped.X - Bounds.X;
        var srcY = clamped.Y - Bounds.Y;

        var dst = new byte[(long)clamped.Width * clamped.Height * 4];
        var rowBytes = clamped.Width * 4;

        for (var row = 0; row < clamped.Height; row++)
        {
            var srcOffset = ((long)(srcY + row) * Width + srcX) * 4;
            var dstOffset = (long)row * rowBytes;
            Array.Copy(Bgra, srcOffset, dst, dstOffset, rowBytes);
        }

        return new CroppedImage(dst, clamped.Width, clamped.Height, HasUncovered(clamped));
    }

    private bool HasUncovered(PixelRect region)
    {
        // Covered area is a union of rectangles, so it is enough to check whether
        // the region's area is fully accounted for by its intersections.
        long covered = 0;
        foreach (var m in Desktop.Monitors)
        {
            var hit = region.Intersect(m.Bounds);
            if (!hit.IsEmpty) covered += (long)hit.Width * hit.Height;
        }
        return covered < (long)region.Width * region.Height;
    }
}
