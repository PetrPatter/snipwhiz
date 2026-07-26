namespace Snipwhiz.Core.Capture;

/// <summary>
/// Cursor at the instant of the grab. Freeze-first makes this unrecoverable
/// afterwards, so it is recorded even though spec 1 renders nothing.
/// Position is virtual-screen physical pixels.
/// </summary>
public readonly record struct CursorState(
    bool Visible, int X, int Y, int HotspotX, int HotspotY, nint Handle)
{
    public static readonly CursorState None = new(false, 0, 0, 0, 0, 0);
}
