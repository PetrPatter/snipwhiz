using System.Windows;
using System.Windows.Input;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Scene;

namespace Snipwhiz.App.Editor.Tools;

/// <summary>
/// Draws any shape that is defined by dragging from one point to another.
///
/// <para>One tool rather than one per shape: what differs between a rectangle, an
/// ellipse and a line is entirely <see cref="Annotation.Fit"/>, which each type
/// already has to answer for its own geometry. Three near-identical tool classes
/// would put the same drag logic in three places and let them drift.</para>
/// </summary>
internal sealed class ShapeTool(
    CanvasHost canvas, SceneDocument document, UndoStack undo,
    Func<Annotation> create, Cursor cursor) : ITool
{
    private Annotation? _drawing;
    private Point _from;

    public Cursor Cursor => cursor;

    /// <summary>Raised once a shape is finished, so the shell can go back to selecting.</summary>
    public event Action<Annotation>? Finished;

    public void OnPress(Point image, ModifierKeys modifiers)
    {
        undo.BeginGesture();
        _from = image;

        // Added at zero size and shown immediately, so the shape is on the canvas
        // from the first pixel of the drag. AddAnnotation absorbs the resizes that
        // follow, so the whole gesture is still one undo step.
        _drawing = create();
        _drawing.ZIndex = NextZ();
        _drawing.Fit(image, image);

        undo.Apply(new AddAnnotation(_drawing));
        canvas.Rebuild();
    }

    public void OnDrag(Point image, ModifierKeys modifiers)
    {
        if (_drawing is null) return;

        var to = (modifiers & ModifierKeys.Shift) != 0 ? Constrain(_from, image) : image;

        var before = _drawing.CaptureGeometry();
        var beforeTransform = _drawing.Transform;
        _drawing.Fit(_from, to);

        undo.Apply(new ResizeAnnotation(
            _drawing, before, beforeTransform, _drawing.CaptureGeometry(), _drawing.Transform));

        canvas.Invalidate(_drawing);
    }

    public void OnRelease(Point image, ModifierKeys modifiers)
    {
        if (_drawing is null) return;
        var finished = _drawing;
        _drawing = null;

        // A click that never became a drag leaves nothing behind. An invisible
        // zero-size object that still hit-tests is worse than no object.
        var bounds = finished.LocalBounds;
        if (bounds.Width < 2 && bounds.Height < 2)
        {
            undo.Undo();
            canvas.Rebuild();
            return;
        }

        canvas.SetSelection([finished]);
        Finished?.Invoke(finished);
    }

    public void Cancel()
    {
        if (_drawing is null) return;
        _drawing = null;
        undo.Undo();
        canvas.Rebuild();
    }

    private int NextZ() =>
        document.Annotations.Count == 0 ? 0 : document.Annotations.Max(a => a.ZIndex) + 1;

    /// <summary>
    /// Shift constrains: a square for box shapes, 15° increments for a line.
    ///
    /// <para>Different meanings, but the same key, because both are "the obvious
    /// tidy version of what I am dragging".</para>
    /// </summary>
    private Point Constrain(Point from, Point to)
    {
        if (_drawing is LineAnnotation)
        {
            var delta = to - from;
            if (delta.Length < 1e-6) return to;
            var step = Math.PI / 12;   // 15°
            var angle = Math.Round(Math.Atan2(delta.Y, delta.X) / step) * step;
            return from + new Vector(Math.Cos(angle), Math.Sin(angle)) * delta.Length;
        }

        var side = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
        return new Point(
            from.X + (to.X < from.X ? -side : side),
            from.Y + (to.Y < from.Y ? -side : side));
    }
}
