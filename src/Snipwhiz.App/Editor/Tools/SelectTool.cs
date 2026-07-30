using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Scene;

namespace Snipwhiz.App.Editor.Tools;

/// <summary>
/// Pick things up, move them, resize them, turn them.
///
/// <para>Every gesture applies its command on <i>every</i> mouse-move rather than
/// buffering until release, so the canvas is correct at each frame. The stack
/// absorbs them into one entry, which is what that mechanism exists for.</para>
/// </summary>
internal sealed class SelectTool(CanvasHost canvas, SceneDocument document, UndoStack undo) : ITool
{
    private enum Gesture { None, Move, Resize, Rotate, Marquee, Control }

    private Gesture _gesture;
    private HandleKind _handle;
    private Annotation? _target;
    private Point _grabImage;
    private Point _marqueeStart;

    // Where the object was when the gesture began, so undo returns there rather
    // than to the previous mouse-move.
    private Matrix _startTransform;
    private GeometryState? _startGeometry;

    public Cursor Cursor => Cursors.Arrow;

    public void OnPress(Point image, ModifierKeys modifiers)
    {
        // A new gesture. Without this the drag folds into whatever the last one
        // was — including the command that created the shape.
        undo.BeginGesture();

        var element = canvas.ToElement(image);

        // A handle belongs to the selected object and is checked first: handles sit
        // on the object's edge, so object hit-testing would swallow every one of them.
        if (canvas.Selection.Count == 1)
        {
            var handle = SelectionOverlay.HandleAt(canvas, canvas.Selection[0], element);
            if (handle is not HandleKind.None)
            {
                // The object says which of its handles are control points; this does
                // not know what a tail is.
                var gesture = canvas.Selection[0].ControlPoints.Contains(handle) ? Gesture.Control
                    : handle is HandleKind.Rotate ? Gesture.Rotate
                    : Gesture.Resize;

                Begin(canvas.Selection[0], gesture, image);
                _handle = handle;
                return;
            }
        }

        var hit = canvas.HitTest(image);

        if (hit is null)
        {
            if ((modifiers & ModifierKeys.Shift) == 0) canvas.ClearSelection();
            _gesture = Gesture.Marquee;
            _marqueeStart = element;
            return;
        }

        if ((modifiers & ModifierKeys.Shift) != 0)
        {
            var next = canvas.Selection.ToList();
            if (!next.Remove(hit)) next.Add(hit);
            canvas.SetSelection(next);
        }
        else if (!canvas.Selection.Contains(hit))
        {
            canvas.SetSelection([hit]);
        }

        Begin(hit, Gesture.Move, image);
    }

    public void OnDrag(Point image, ModifierKeys modifiers)
    {
        switch (_gesture)
        {
            case Gesture.Marquee:
                canvas.Marquee = new Rect(_marqueeStart, canvas.ToElement(image));
                canvas.RefreshOverlay();
                break;

            case Gesture.Move:
                MoveSelection(image);
                break;

            case Gesture.Resize:
                ResizeTarget(image, modifiers);
                break;

            case Gesture.Rotate:
                RotateTarget(image, modifiers);
                break;

            case Gesture.Control:
                DragControlPoint(image);
                break;
        }
    }

    public void OnRelease(Point image, ModifierKeys modifiers)
    {
        if (_gesture is Gesture.Marquee) CommitMarquee();

        _gesture = Gesture.None;
        _target = null;
        _startGeometry = null;
        canvas.Marquee = null;
        canvas.RefreshOverlay();
    }

    public void Cancel()
    {
        // The gesture was applied as commands, so taking it back is an undo rather
        // than a restore — and absorbing means the whole drag is one step.
        if (_gesture is Gesture.Move or Gesture.Resize or Gesture.Rotate or Gesture.Control) undo.Undo();

        _gesture = Gesture.None;
        _target = null;
        canvas.Marquee = null;
        canvas.RefreshOverlay();
    }

    // ---- gestures ---------------------------------------------------------

    private void Begin(Annotation target, Gesture gesture, Point image)
    {
        _gesture = gesture;
        _target = target;
        _grabImage = image;
        _startTransform = target.Transform;
        _startGeometry = target.CaptureGeometry();
    }

    private void MoveSelection(Point image)
    {
        if (_target is null) return;

        var delta = image - _grabImage;
        foreach (var annotation in canvas.Selection)
        {
            var moved = annotation.Transform;
            moved.OffsetX += delta.X;
            moved.OffsetY += delta.Y;
            undo.Apply(new MoveAnnotation(annotation, annotation.Transform, moved));
            canvas.Invalidate(annotation);
        }

        _grabImage = image;
        canvas.RefreshOverlay();
    }

    private void ResizeTarget(Point image, ModifierKeys modifiers)
    {
        if (_target is null || _startGeometry is null) return;

        // Into the object's own frame, where the maths is tractable and where
        // Handles was tested.
        var inverse = _startTransform;
        if (!inverse.HasInverse) return;
        inverse.Invert();

        var resized = Handles.Resize(
            _target, _handle, inverse.Transform(image),
            preserveAspect: (modifiers & ModifierKeys.Shift) != 0,
            aboutCentre: (modifiers & ModifierKeys.Alt) != 0);

        // The shape decides what its bounds mean. This used to build a rectangle's
        // state unconditionally, which would have thrown the first time anyone
        // resized an ellipse — and silently flipped a line end for end.
        undo.Apply(new ResizeAnnotation(
            _target, _startGeometry, _startTransform,
            _target.GeometryForBounds(resized.Size), resized.Transform));

        canvas.Invalidate(_target);
        canvas.RefreshOverlay();
    }

    /// <summary>
    /// Drags a control point — today only a callout's tail.
    ///
    /// <para>A <see cref="ReshapeAnnotation"/>, not a resize: the object's bounds and
    /// transform do not move, only its own idea of its shape. That is also what makes
    /// the whole drag one undo step, since reshapes absorb.</para>
    /// </summary>
    private void DragControlPoint(Point image)
    {
        if (_target is null || _startGeometry is null) return;

        var inverse = _startTransform;
        if (!inverse.HasInverse) return;
        inverse.Invert();

        if (_target.MoveControlPoint(_handle, inverse.Transform(image)) is not { } moved) return;

        undo.Apply(new ReshapeAnnotation(_target, _startGeometry, moved));
        canvas.Invalidate(_target);
        canvas.RefreshOverlay();
    }

    private void RotateTarget(Point image, ModifierKeys modifiers)
    {
        if (_target is null) return;

        var rotated = Handles.RotateToward(
            _target, image, snapDegrees: (modifiers & ModifierKeys.Shift) != 0 ? 15 : 0);

        undo.Apply(new MoveAnnotation(_target, _startTransform, rotated));
        canvas.Invalidate(_target);
        canvas.RefreshOverlay();
    }

    private void CommitMarquee()
    {
        if (canvas.Marquee is not { } band) return;

        // Element space to image space, so the comparison happens where the
        // geometry lives.
        var region = new Rect(canvas.ToImage(band.TopLeft), canvas.ToImage(band.BottomRight));
        canvas.SetSelection(document.Annotations.Where(a => region.IntersectsWith(a.Bounds)));
    }
}
