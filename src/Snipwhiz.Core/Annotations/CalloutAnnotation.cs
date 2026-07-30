using System.Windows;
using System.Windows.Media;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// A caption with a tail, so it can say what it is about.
///
/// <para>A <see cref="TextAnnotation"/> underneath, and that is the whole point of
/// doing it last: it inherits the measuring, the editing overlay and the metrics
/// seam §4.8 spent a task getting right. If any of that had needed reimplementing,
/// that would have been a finding about B5's design rather than about callouts.</para>
///
/// <para><b>The tail is local.</b> Moving the bubble carries it, which is what a
/// callout does — the tail is part of the object, not an anchor into the picture.
/// (A magnifier's subject is the opposite case and is absolute, for the opposite
/// reason.) Re-aiming is the tail handle, the first control point that is neither a
/// resizer nor the rotate handle.</para>
/// </summary>
public sealed class CalloutAnnotation : TextAnnotation
{
    /// <summary>Down and to the left, so a new callout points at something below it.</summary>
    public static readonly Vector DefaultTail = new(-40, 56);

    /// <summary>
    /// How wide the tail is where it leaves the bubble, in image pixels. Wide enough
    /// to read as part of the bubble rather than as a line touching it.
    /// </summary>
    private const double RootWidth = 18;

    /// <summary>The tip, relative to the bubble's centre.</summary>
    public Vector Tail { get; set; } = DefaultTail;

    public override IReadOnlyList<HandleKind> ControlPoints => [HandleKind.Tail];

    public override Point ControlPoint(HandleKind kind) =>
        kind is HandleKind.Tail ? new Point(Tail.X, Tail.Y) : base.ControlPoint(kind);

    public override GeometryState? MoveControlPoint(HandleKind kind, Point local) =>
        kind is HandleKind.Tail
            ? new CalloutGeometryState(Text, FontSize, new Vector(local.X, local.Y))
            : null;

    public override GeometryState CaptureGeometry() => new CalloutGeometryState(Text, FontSize, Tail);

    public override void RestoreGeometry(GeometryState state)
    {
        var callout = (CalloutGeometryState)state;
        Text = callout.Text;
        FontSize = callout.FontSize;
        Tail = callout.Tail;
    }

    /// <summary>
    /// Resizing changes the font size, exactly as text does, and carries the tail
    /// through unchanged.
    ///
    /// <para>Unchanged <i>here</i> is not the same as unmoved on screen: dragging a
    /// corner anchors the opposite one and slides the bubble's centre, and the tail
    /// is measured from that centre. <see cref="Rebased"/> is what takes the slide
    /// back out.</para>
    /// </summary>
    public override GeometryState GeometryForBounds(Size size)
    {
        var resized = (TextGeometryState)base.GeometryForBounds(size);
        return new CalloutGeometryState(resized.Text, resized.FontSize, Tail);
    }

    /// <summary>
    /// Keeps the tip where it was pointing while the bubble grows away from it.
    ///
    /// <para>The new origin sits at <paramref name="localShift"/> in the old frame,
    /// so everything measured from the old origin is that much further away in the
    /// new one. Subtracting it is the whole fix.</para>
    /// </summary>
    public override GeometryState Rebased(GeometryState state, Vector localShift) =>
        state is CalloutGeometryState callout
            ? callout with { Tail = callout.Tail - localShift }
            : state;

    /// <summary>
    /// The bubble and the tail as one geometry.
    ///
    /// <para>United rather than grouped. Filled with one brush the two look identical,
    /// which is exactly why it is worth doing properly: the plate is translucent by
    /// default, and the day anything strokes this outline a group would draw a line
    /// straight across the mouth of the tail.</para>
    /// </summary>
    protected override System.Windows.Media.Geometry Plate(Rect bounds)
    {
        var bubble = new RectangleGeometry(bounds, CornerRadius, CornerRadius);

        var tip = new Point(Tail.X, Tail.Y);
        var root = Root(bounds, tip);

        var tail = new StreamGeometry();
        using (var draw = tail.Open())
        {
            draw.BeginFigure(root.From, isFilled: true, isClosed: true);
            draw.LineTo(tip, isStroked: true, isSmoothJoin: false);
            draw.LineTo(root.To, isStroked: true, isSmoothJoin: false);
        }
        tail.Freeze();

        var combined = new CombinedGeometry(GeometryCombineMode.Union, bubble, tail);
        combined.Freeze();
        return combined;
    }

    /// <summary>
    /// Where the tail leaves the bubble: a short span on the edge nearest the tip,
    /// square to the direction it points.
    ///
    /// <para>Kept <b>inside</b> the bubble rather than exactly on its edge, so the
    /// union has real overlap to merge. A root sitting precisely on a rounded corner
    /// can otherwise miss the geometry entirely and leave the tail floating.</para>
    /// </summary>
    private static (Point From, Point To) Root(Rect bounds, Point tip)
    {
        var direction = tip - new Point(0, 0);
        if (direction.Length < 1e-6) direction = new Vector(0, 1);
        direction.Normalize();

        // Square to the direction of the tail, so the mouth is widest across the way
        // it points rather than always horizontal.
        var across = new Vector(-direction.Y, direction.X) * (RootWidth / 2);

        // Just inside the bubble, along the line toward the tip.
        var inset = Math.Min(bounds.Width, bounds.Height) / 2;
        var mouth = new Point(direction.X * inset * 0.6, direction.Y * inset * 0.6);

        return (mouth - across, mouth + across);
    }

    /// <summary>
    /// The tail counts as part of the object, or a callout can only be picked up by
    /// its bubble and the tail becomes decoration you cannot grab.
    /// </summary>
    protected override bool HitTestLocal(Point local, double tolerance)
    {
        if (base.HitTestLocal(local, tolerance)) return true;

        // Distance to the segment from the bubble's centre to the tip, which is the
        // tail's spine. The same clamped projection LineAnnotation uses.
        var tip = new Vector(Tail.X, Tail.Y);
        var lengthSquared = tip.LengthSquared;
        if (lengthSquared < 1e-6) return false;

        var t = Math.Clamp((local.X * tip.X + local.Y * tip.Y) / lengthSquared, 0, 1);
        var nearest = new Point(tip.X * t, tip.Y * t);

        // Widest at the root and pointed at the tip, so the grab region follows the
        // shape rather than being a fat line the whole way along.
        var reach = RootWidth / 2 * (1 - t) + tolerance;
        return (local - nearest).LengthSquared <= reach * reach;
    }
}
