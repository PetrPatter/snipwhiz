using Snipwhiz.Core.Geometry;

namespace Snipwhiz.App;

/// <summary>
/// The single source of truth for the selection, in virtual physical pixels.
/// Overlays render their intersection with it; none of them owns it.
/// </summary>
public sealed class SelectionController(CaptureSession session)
{
    private int _anchorX, _anchorY;
    private bool _dragging;

    public PixelRect? Current { get; private set; }
    public bool IsDragging => _dragging;

    public event Action<PixelRect?>? Changed;

    public void BeginDrag(int virtualX, int virtualY)
    {
        _anchorX = virtualX;
        _anchorY = virtualY;
        _dragging = true;
        Current = null;
        Changed?.Invoke(Current);
    }

    public void UpdateDrag(int virtualX, int virtualY)
    {
        if (!_dragging) return;
        Current = PixelRect
            .FromCorners(_anchorX, _anchorY, virtualX, virtualY)
            .ClampTo(session.Frozen.Bounds);
        Changed?.Invoke(Current);
    }

    /// <returns>The committed rect, or null if the drag was too small to be intentional.</returns>
    public PixelRect? EndDrag()
    {
        if (!_dragging) return null;
        _dragging = false;

        // A click without a drag is not a selection.
        if (Current is not { } rect || rect.Width < 3 || rect.Height < 3)
        {
            Current = null;
            Changed?.Invoke(null);
            return null;
        }
        return rect;
    }
}
