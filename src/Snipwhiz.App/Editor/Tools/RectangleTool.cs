using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Scene;

namespace Snipwhiz.App.Editor.Tools;

/// <summary>
/// Draws a rectangle by dragging.
///
/// <para>Phase A's proving tool: the cheapest thing that exercises create, select,
/// resize, rotate, undo, serialize, flatten and library refresh end to end, with no
/// text entry or pixel sampling to debug alongside the foundation.</para>
/// </summary>
internal sealed class RectangleTool(CanvasHost canvas, SceneDocument document, UndoStack undo) : ITool
{
    private RectangleAnnotation? _drawing;
    private Point _from;

    public Cursor Cursor => Cursors.Cross;

    /// <summary>Raised once a shape is finished, so the window can go back to selecting.</summary>
    public event Action<Annotation>? Finished;

    public void OnPress(Point image, ModifierKeys modifiers)
    {
        undo.BeginGesture();
        _from = image;

        // Created at zero size and added immediately, so the shape is on the canvas
        // and visible from the first pixel of the drag. AddAnnotation absorbs the
        // resizes that follow, so the whole thing is still one undo step.
        _drawing = new RectangleAnnotation
        {
            Size = new Size(0, 0),
            Transform = new Matrix(1, 0, 0, 1, image.X, image.Y),
            ZIndex = NextZ(),
        };

        undo.Apply(new AddAnnotation(_drawing));
        canvas.Rebuild();
    }

    public void OnDrag(Point image, ModifierKeys modifiers)
    {
        if (_drawing is null) return;

        var to = image;
        if ((modifiers & ModifierKeys.Shift) != 0) to = Square(_from, image);

        var rect = new Rect(_from, to);
        undo.Apply(new ResizeAnnotation(
            _drawing,
            _drawing.CaptureGeometry(), _drawing.Transform,
            new RectangleGeometryState(rect.Size),
            new Matrix(1, 0, 0, 1, rect.X + rect.Width / 2, rect.Y + rect.Height / 2)));

        canvas.Invalidate(_drawing);
    }

    public void OnRelease(Point image, ModifierKeys modifiers)
    {
        if (_drawing is null) return;
        var finished = _drawing;
        _drawing = null;

        // A click that never became a drag leaves nothing behind. An invisible
        // zero-size object that still hit-tests is worse than no object.
        if (finished.Size.Width < 2 || finished.Size.Height < 2)
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

    /// <summary>Shift constrains to a square, sized by the longer edge and following the pointer.</summary>
    private static Point Square(Point from, Point to)
    {
        var side = Math.Max(Math.Abs(to.X - from.X), Math.Abs(to.Y - from.Y));
        return new Point(
            from.X + Math.Sign(to.X - from.X) * side,
            from.Y + Math.Sign(to.Y - from.Y) * side);
    }
}
