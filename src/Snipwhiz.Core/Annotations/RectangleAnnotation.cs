using System.Windows;
using System.Windows.Media;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// Phase A's proving tool. Deliberately the cheapest thing that exercises the
/// whole foundation — create by drag, hit-test, select, resize from eight handles,
/// rotate, undo, serialize, flatten, library refresh — with no text entry, pixel
/// sampling or geometry subtleties to debug alongside it.
/// </summary>
public sealed class RectangleAnnotation : Annotation
{
    /// <summary>
    /// Size in image pixels. Resizing changes this rather than a scale in
    /// <see cref="Annotation.Transform"/>, so the stroke stays the width the user
    /// picked.
    /// </summary>
    public Size Size { get; set; }

    public override Rect LocalBounds =>
        new(-Size.Width / 2, -Size.Height / 2, Size.Width, Size.Height);

    /// <summary>Places a rectangle spanning two image-space points, unrotated.</summary>
    public static RectangleAnnotation FromDrag(Point from, Point to, AnnotationStyle? style = null)
    {
        var shape = new RectangleAnnotation { Style = style ?? AnnotationStyle.Default };
        shape.Fit(from, to);
        return shape;
    }

    public override void Fit(Point from, Point to)
    {
        var rect = new Rect(from, to);
        Size = rect.Size;
        Transform = new Matrix(1, 0, 0, 1, rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    public override GeometryState GeometryForBounds(Size size) => new RectangleGeometryState(size);

    /// <summary>
    /// The interior counts, not just the outline.
    ///
    /// <para>Strictly, an unfilled shape is only its stroke, and that is what a
    /// vector editor would do. In an annotation tool it means clicking the middle
    /// of a rectangle you just drew selects whatever is behind it, which reads as
    /// the app ignoring you. If a later phase needs true outline-only picking it
    /// can branch on <see cref="Style.Fill"/>.</para>
    /// </summary>
    protected override bool HitTestLocal(Point local, double tolerance)
    {
        var hit = LocalBounds;
        // Half the stroke sits outside the geometry, so a click on the visible edge
        // of a thick outline is still a click on the shape.
        var slack = tolerance + Style.StrokeWidth / 2;
        hit.Inflate(slack, slack);
        return hit.Contains(local);
    }

    public override GeometryState CaptureGeometry() => new RectangleGeometryState(Size);

    public override void RestoreGeometry(GeometryState state) =>
        Size = ((RectangleGeometryState)state).Size;

    public override void Render(DrawingContext dc)
    {
        dc.PushTransform(new MatrixTransform(Transform));
        dc.DrawRectangle(FillBrush(), StrokePen(), LocalBounds);
        dc.Pop();
    }
}
