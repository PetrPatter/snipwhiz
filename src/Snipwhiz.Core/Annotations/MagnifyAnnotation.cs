using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// Shows part of the capture enlarged.
///
/// <para><b>Two rectangles, not one.</b> Where it samples from and where it draws to
/// are separate, and that separation is the entire tool: a lens welded to the thing
/// it magnifies can only ever cover its own subject, which is a way of hiding
/// something with a bigger copy of it.</para>
///
/// <para>The lens is this annotation's own geometry — position, size and rotation,
/// like any other object. <see cref="SourceCentre"/> is an <b>absolute</b> point in
/// image space and does not move with it, so drawing a magnifier and then dragging
/// it aside is what separates the two, with no second gesture to learn.</para>
///
/// <para><b>Not yet re-aimable.</b> Moving the lens moves the lens; there is no way
/// to leave the lens and move the source. That needs a control point which is
/// neither a resize handle nor the rotate handle, which is exactly what the callout
/// tail introduces in the next task — so it waits for that rather than growing a
/// private one here.</para>
/// </summary>
public sealed class MagnifyAnnotation : RectangleAnnotation
{
    public const double DefaultZoom = 2;

    public double Zoom { get; set; } = DefaultZoom;

    /// <summary>
    /// The middle of the sampled region, in image pixels — absolute, not relative to
    /// the lens.
    /// </summary>
    public Point SourceCentre { get; set; }

    public MagnifyAnnotation() =>
        // A border, because a magnified patch dropped on a screenshot with no edge
        // reads as a rendering fault rather than as a deliberate close-up.
        Style = new AnnotationStyle { Stroke = Colors.White, StrokeWidth = 3 };

    /// <summary>Drawn over what it magnifies, so it starts as a plain close-up in place.</summary>
    public override void Fit(Point from, Point to)
    {
        base.Fit(from, to);
        var rect = new Rect(from, to);
        SourceCentre = new Point(rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    public override double SizeControl
    {
        get => Zoom;
        set => Zoom = value;
    }

    /// <summary>
    /// Below about 1.5 there is nothing to see and the object is a bordered copy of
    /// what is already there; past 8 a screenshot's pixels are the subject.
    /// </summary>
    public override (double Min, double Max) SizeControlRange => (2, 8);

    public override string SizeControlLabel => "Magnification";

    public override GeometryState CaptureGeometry() =>
        new MagnifyGeometryState(Size, Zoom, SourceCentre);

    public override void RestoreGeometry(GeometryState state)
    {
        var magnify = (MagnifyGeometryState)state;
        Size = magnify.Size;
        Zoom = magnify.Zoom;
        SourceCentre = magnify.SourceCentre;
    }

    /// <summary>
    /// Resizing changes the lens and leaves the subject alone — a bigger lens at the
    /// same magnification shows more of what it is pointed at, which is what a lens
    /// does.
    /// </summary>
    public override GeometryState GeometryForBounds(Size size) =>
        new MagnifyGeometryState(size, Zoom, SourceCentre);

    public override void Render(DrawingContext dc, BitmapSource source)
    {
        var region = SourceRegion(source);
        var patch = new CroppedBitmap(source, region);

        dc.PushTransform(new MatrixTransform(Transform));

        // Clipped, then the border drawn after the clip is popped, so a thick border
        // sits on the edge rather than having its outer half shaved off.
        dc.PushClip(new RectangleGeometry(LocalBounds));
        dc.DrawImage(patch, LocalBounds);
        dc.Pop();

        dc.DrawRectangle(null, StrokePen(), LocalBounds);

        dc.Pop();
    }

    /// <summary>
    /// The patch to enlarge: the lens divided by the magnification, centred on
    /// <see cref="SourceCentre"/> and kept inside the capture.
    ///
    /// <para>Pushed back inside rather than clipped to fit. Clipping would change the
    /// patch's aspect ratio and <see cref="DrawingContext.DrawImage"/> would stretch
    /// it to the lens, so a magnifier aimed near an edge would show a subtly
    /// distorted close-up — wrong in a way that looks like bad focus rather than like
    /// a bug.</para>
    /// </summary>
    private Int32Rect SourceRegion(BitmapSource source)
    {
        var width = Math.Clamp((int)Math.Round(Size.Width / Zoom), 1, source.PixelWidth);
        var height = Math.Clamp((int)Math.Round(Size.Height / Zoom), 1, source.PixelHeight);

        var x = Math.Clamp(
            (int)Math.Round(SourceCentre.X - width / 2.0), 0, source.PixelWidth - width);
        var y = Math.Clamp(
            (int)Math.Round(SourceCentre.Y - height / 2.0), 0, source.PixelHeight - height);

        return new Int32Rect(x, y, width, height);
    }
}
