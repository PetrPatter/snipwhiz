using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// A line with a filled head at the end it was dragged to.
///
/// <para>Inherits <see cref="LineAnnotation"/> rather than repeating it: the
/// geometry is the same vector, and the direction-preserving resize matters
/// <i>more</i> here — a line that comes back reversed looks like a line, an arrow
/// that comes back reversed points at the wrong thing.</para>
/// </summary>
public sealed class ArrowAnnotation : LineAnnotation
{
    /// <summary>
    /// Head size as a multiple of the stroke width, so a thick arrow does not get a
    /// pinhead. A head fixed in pixels looks right at exactly one stroke width.
    /// </summary>
    private const double LengthPerStrokeWidth = 4;
    private const double HalfWidthPerStrokeWidth = 2;

    /// <summary>Past this the head has eaten the arrow and there is no shaft left to read.</summary>
    private const double MaxShareOfLength = 0.6;

    /// <summary>
    /// One source for the head's shape, used by both <see cref="Render"/> and
    /// <see cref="HitTestLocal"/> — a head you can see but not click, or click but
    /// not see, is what two copies of this produce.
    /// </summary>
    private (double Length, double HalfWidth) Head()
    {
        var width = Math.Max(Style.StrokeWidth, 1);
        var length = width * LengthPerStrokeWidth;
        var halfWidth = width * HalfWidthPerStrokeWidth;

        // Shrunk in proportion, not clipped: a short arrow gets a small head rather
        // than a stubby wide one.
        var span = Delta.Length;
        if (span > 0 && length > span * MaxShareOfLength)
        {
            var scale = span * MaxShareOfLength / length;
            length *= scale;
            halfWidth *= scale;
        }

        return (length, halfWidth);
    }

    /// <summary>
    /// Draws the shaft up to the head's base and the head from there to the tip.
    ///
    /// <para><b>The shaft stops short.</b> Running it the full length puts a bar of
    /// stroke width across the point, which reads as blunt — invisible at 2px and
    /// obvious at 12. The head's apex is exactly the point the drag ended on.</para>
    /// </summary>
    public override void Render(DrawingContext dc, BitmapSource source)
    {
        var pen = StrokePen();
        if (pen is null) return;

        var span = Delta.Length;
        if (span < 1e-6) return;

        var direction = Delta / span;
        var normal = new Vector(-direction.Y, direction.X);
        var (length, halfWidth) = Head();

        var tip = End;
        var back = tip - direction * length;

        dc.PushTransform(new MatrixTransform(Transform));
        dc.DrawLine(pen, Start, back);

        var head = new StreamGeometry();
        using (var figure = head.Open())
        {
            figure.BeginFigure(tip, isFilled: true, isClosed: true);
            figure.LineTo(back + normal * halfWidth, isStroked: false, isSmoothJoin: false);
            figure.LineTo(back - normal * halfWidth, isStroked: false, isSmoothJoin: false);
        }
        head.Freeze();

        // The pen's brush, so the head carries the stroke colour and opacity without
        // a second place to keep them in step.
        dc.DrawGeometry(pen.Brush, null, head);
        dc.Pop();
    }

    /// <summary>
    /// The head is wider than the shaft, so the segment test alone leaves the part
    /// of the arrow the eye reads as the arrow unclickable.
    /// </summary>
    protected override bool HitTestLocal(Point local, double tolerance)
    {
        if (base.HitTestLocal(local, tolerance)) return true;

        var span = Delta.Length;
        if (span < 1e-6) return false;

        var direction = Delta / span;
        var (length, halfWidth) = Head();

        // Measured back from the tip, along the arrow's axis and across it.
        var offset = local - End;
        var along = -(offset * direction);
        var across = Math.Abs(offset * new Vector(-direction.Y, direction.X));

        if (along < -tolerance || along > length + tolerance) return false;
        return across <= halfWidth * Math.Clamp(along / length, 0, 1) + tolerance;
    }
}
