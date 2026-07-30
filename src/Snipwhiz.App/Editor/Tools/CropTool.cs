using System.Windows;
using System.Windows.Input;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Scene;

namespace Snipwhiz.App.Editor.Tools;

/// <summary>
/// Chooses which part of the capture is the picture.
///
/// <para><b>Nothing is destroyed.</b> The crop is a rectangle on the document, the
/// original file is never rewritten, and annotations outside it stay in the project
/// — they are clipped from the render, not deleted. Widening the crop next week
/// brings the picture and everything on it back whole, which is the entire reason
/// the format is non-destructive.</para>
///
/// <para>While this tool is active the canvas shows the whole capture with the
/// outside dimmed, so the part being cropped away is still there to drag back.</para>
/// </summary>
internal sealed class CropTool(CanvasHost canvas, SceneDocument document, UndoStack undo) : ITool
{
    private enum Gesture { None, New, Move, Resize }

    private Gesture _gesture;
    private HandleKind _handle;
    private Point _grab;
    private Rect _startRect;

    public Cursor Cursor => Cursors.Cross;

    /// <summary>The rectangle being edited, defaulting to the whole capture.</summary>
    private Rect Current =>
        canvas.CropPreview ?? document.Crop ?? new Rect(new Point(0, 0), canvas.ImageSize);

    /// <summary>Shows the whole capture, dimmed outside the crop, and takes the selection away.</summary>
    public void Activate()
    {
        canvas.ShowUncropped = true;
        canvas.ClearSelection();
        SyncPreview();
        canvas.Fit();
    }

    /// <summary>
    /// Re-reads the crop from the document. Undo and redo change it underneath this
    /// tool, and the dimmed rectangle is drawn from the preview, not the document.
    /// </summary>
    public void SyncPreview()
    {
        canvas.CropPreview = document.Crop ?? new Rect(new Point(0, 0), canvas.ImageSize);
        canvas.RefreshOverlay();
    }

    /// <summary>Back to showing only the crop. The crop itself is already in the document.</summary>
    public void Deactivate()
    {
        canvas.ShowUncropped = false;
        canvas.CropPreview = null;
        canvas.Fit();
    }

    public void OnPress(Point image, ModifierKeys modifiers)
    {
        undo.BeginGesture();

        var rect = Current;
        _startRect = rect;
        _grab = image;

        var handle = HandleAt(rect, canvas.ToElement(image));
        if (handle is not HandleKind.None)
        {
            _gesture = Gesture.Resize;
            _handle = handle;
            return;
        }

        // Inside moves the whole rectangle; outside starts a fresh one, which is how
        // you get a small crop out of a big capture without dragging four edges.
        _gesture = rect.Contains(image) ? Gesture.Move : Gesture.New;
        if (_gesture == Gesture.New) Apply(new Rect(image, image));
    }

    public void OnDrag(Point image, ModifierKeys modifiers)
    {
        switch (_gesture)
        {
            case Gesture.New:
                Apply(new Rect(_grab, image));
                break;

            case Gesture.Move:
                var delta = image - _grab;
                Apply(Rect.Offset(_startRect, delta.X, delta.Y));
                break;

            case Gesture.Resize:
                // Through Handles, so the eight-handle arithmetic — including which
                // corner anchors, Shift for aspect and Alt for about-centre — is the
                // one already used by the selection tool.
                var proxy = CropProxy.For(_startRect);
                var inverse = proxy.Transform;
                if (!inverse.HasInverse) return;
                inverse.Invert();

                var resized = Handles.Resize(
                    proxy, _handle, inverse.Transform(image),
                    preserveAspect: (modifiers & ModifierKeys.Shift) != 0,
                    aboutCentre: (modifiers & ModifierKeys.Alt) != 0,
                    minimum: 8);

                proxy.Resize(resized.Size, resized.Transform);
                Apply(proxy.Rect);
                break;
        }
    }

    public void OnRelease(Point image, ModifierKeys modifiers) => _gesture = Gesture.None;

    /// <summary>Escape puts the crop back where the gesture found it.</summary>
    public void Cancel()
    {
        if (_gesture == Gesture.None) return;
        _gesture = Gesture.None;
        Apply(_startRect);
    }

    /// <summary>
    /// Clamps to the capture and applies. A crop reaching outside the picture would
    /// export transparent margins, and one dragged inside-out would be empty.
    /// </summary>
    private void Apply(Rect rect)
    {
        var full = new Rect(new Point(0, 0), canvas.ImageSize);
        rect.Intersect(full);
        if (rect.IsEmpty || rect.Width < 8 || rect.Height < 8) return;

        canvas.CropPreview = rect;

        // Applied on every move rather than buffered until release, so the dim and
        // the document never disagree. CropDocument absorbs, so the gesture is one
        // undo step.
        undo.Apply(new CropDocument(document.Crop, rect));
        canvas.RefreshOverlay();
    }

    private HandleKind HandleAt(Rect rect, Point element)
    {
        var handle = SelectionOverlay.HandleAt(canvas, CropProxy.For(rect), element);

        // A crop cannot be turned. The rotate handle would otherwise sit above the
        // top edge stealing grabs for a gesture that does not exist here.
        return handle is HandleKind.Rotate ? HandleKind.None : handle;
    }
}
