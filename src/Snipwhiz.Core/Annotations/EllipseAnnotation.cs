using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// An ellipse inscribed in its bounds.
///
/// <para>Shares the centred-box geometry with <see cref="RectangleAnnotation"/> and
/// differs only in how it draws and what it considers a hit — which is the point of
/// building it: it is the cheapest check that the model generalises at all.</para>
/// </summary>
public sealed class EllipseAnnotation : Annotation
{
    public Size Size { get; set; }

    public override Rect LocalBounds =>
        new(-Size.Width / 2, -Size.Height / 2, Size.Width, Size.Height);

    public override void Fit(Point from, Point to)
    {
        var rect = new Rect(from, to);
        Size = rect.Size;
        Transform = new Matrix(1, 0, 0, 1, rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    public override GeometryState CaptureGeometry() => new EllipseGeometryState(Size);

    public override void RestoreGeometry(GeometryState state) =>
        Size = ((EllipseGeometryState)state).Size;

    public override GeometryState GeometryForBounds(Size size) => new EllipseGeometryState(size);

    /// <summary>
    /// Inside the ellipse, not inside its box.
    ///
    /// <para>The distinction is the whole reason this type is not a rectangle with a
    /// different <c>Render</c>. Near a corner of the bounds an ellipse is nowhere
    /// near the pointer, and a bounds check there selects a shape the user can
    /// plainly see they did not click.</para>
    ///
    /// <para>Measured as a normalised radius: each axis is divided by its own
    /// semi-axis, which turns the ellipse into a unit circle and the test into a
    /// comparison against 1.</para>
    /// </summary>
    protected override bool HitTestLocal(Point local, double tolerance)
    {
        var slack = tolerance + Style.StrokeWidth / 2;
        var rx = Size.Width / 2 + slack;
        var ry = Size.Height / 2 + slack;
        if (rx <= 0 || ry <= 0) return false;

        var x = local.X / rx;
        var y = local.Y / ry;
        return x * x + y * y <= 1;
    }

    public override void Render(DrawingContext dc, BitmapSource source)
    {
        dc.PushTransform(new MatrixTransform(Transform));
        dc.DrawEllipse(FillBrush(), StrokePen(), new Point(0, 0), Size.Width / 2, Size.Height / 2);
        dc.Pop();
    }
}
