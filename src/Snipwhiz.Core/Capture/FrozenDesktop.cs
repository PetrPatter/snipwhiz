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
}
