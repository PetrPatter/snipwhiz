using System.Windows;
using System.Windows.Media;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// A straight line.
///
/// <para><b>The shape that breaks the centred-box assumption</b>, which is why it
/// is in the first task of this phase rather than a later one. Its geometry is a
/// vector, its bounds are derived rather than stored, and its hit region is a
/// distance to a segment — a box test would claim the entire empty area under a
/// diagonal.</para>
///
/// <para>Stored centred on its own midpoint, running from <c>-Delta/2</c> to
/// <c>+Delta/2</c>, so rotation turns it about its middle exactly like every other
/// annotation and needs no special case.</para>
/// </summary>
public sealed class LineAnnotation : Annotation
{
    /// <summary>End minus start. Its sign carries the direction, which resizing must not flip.</summary>
    public Vector Delta { get; set; }

    public Point Start => new(-Delta.X / 2, -Delta.Y / 2);

    public Point End => new(Delta.X / 2, Delta.Y / 2);

    public override Rect LocalBounds =>
        new(-Math.Abs(Delta.X) / 2, -Math.Abs(Delta.Y) / 2, Math.Abs(Delta.X), Math.Abs(Delta.Y));

    public override void Fit(Point from, Point to)
    {
        Delta = to - from;
        Transform = new Matrix(1, 0, 0, 1, (from.X + to.X) / 2, (from.Y + to.Y) / 2);
    }

    public override GeometryState CaptureGeometry() => new LineGeometryState(Delta);

    public override void RestoreGeometry(GeometryState state) =>
        Delta = ((LineGeometryState)state).Delta;

    /// <summary>
    /// Keeps the direction and changes only the extent.
    ///
    /// <para>Handles describe a box, and a line has to read one without flipping
    /// end for end. <c>Math.Sign</c> is deliberately not used: it returns zero for a
    /// perfectly horizontal or vertical line and would collapse the other axis to
    /// nothing.</para>
    /// </summary>
    public override GeometryState GeometryForBounds(Size size) =>
        new LineGeometryState(new Vector(
            Delta.X < 0 ? -size.Width : size.Width,
            Delta.Y < 0 ? -size.Height : size.Height));

    /// <summary>
    /// Distance to the segment, not containment in the bounds.
    ///
    /// <para>A diagonal line's bounding box is mostly empty. Testing the box makes
    /// every one of those empty pixels select the line, which reads as the app
    /// grabbing things that are not under the pointer.</para>
    /// </summary>
    protected override bool HitTestLocal(Point local, double tolerance)
    {
        var slack = tolerance + Math.Max(Style.StrokeWidth / 2, 1);
        var start = Start;
        var span = Delta;

        var lengthSquared = span.LengthSquared;
        if (lengthSquared < 1e-9) return (local - start).Length <= slack;

        // Project onto the segment and clamp, so the ends are round rather than
        // extending to an infinite line.
        var t = Math.Clamp(((local - start) * span) / lengthSquared, 0, 1);
        var nearest = start + span * t;
        return (local - nearest).Length <= slack;
    }

    public override void Render(DrawingContext dc)
    {
        var pen = StrokePen();
        if (pen is null) return;

        dc.PushTransform(new MatrixTransform(Transform));
        dc.DrawLine(pen, Start, End);
        dc.Pop();
    }
}
