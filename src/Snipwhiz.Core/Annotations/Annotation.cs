using System.Windows;
using System.Windows.Media;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// One object on the canvas.
///
/// <para>Lives in Core, not App, because <see cref="Render"/> is called by both the
/// on-screen canvas and the flattener. Two render implementations is how "export
/// doesn't match what I drew" becomes a bug class; one is how it becomes
/// impossible. Spec 2b §1.</para>
///
/// <para><b>Geometry is in image-pixel space</b> (spec §4.3), so zoom, window
/// resize, DPI changes and flattening are all exact — the canvas applies one scale
/// transform and the flattener applies none.</para>
/// </summary>
public abstract class Annotation
{
    public Guid Id { get; init; } = Guid.CreateVersion7();

    public int ZIndex { get; set; }

    /// <summary>
    /// Where this object sits and how it is turned, in image space.
    ///
    /// <para><b>Invariant: rotation and translation only — never scale.</b> Resizing
    /// edits the object's own geometry instead, which is what keeps a stroke the
    /// width the user chose when they make a shape bigger. It also keeps this a
    /// rigid motion, so a hit-test tolerance means the same distance in local space
    /// as it does in image space and <see cref="HitTest"/> needs no correction.</para>
    ///
    /// <para>A phase that introduces scale here — multi-select resize is the likely
    /// one — has to revisit <see cref="HitTest"/> and the stroke width used by every
    /// <see cref="Render"/>.</para>
    /// </summary>
    public Matrix Transform { get; set; } = Matrix.Identity;

    public AnnotationStyle Style { get; set; } = AnnotationStyle.Default;

    /// <summary>
    /// The object's extent in its own space, centred on the origin.
    ///
    /// <para>Centred rather than origin-at-top-left so that rotation in
    /// <see cref="Transform"/> turns the object about its own middle, which is what
    /// a rotate handle is expected to do, with no correction term.</para>
    /// </summary>
    public abstract Rect LocalBounds { get; }

    /// <summary>Axis-aligned bounding box in image space.</summary>
    public Rect Bounds => Rect.Transform(LocalBounds, Transform);

    /// <summary>Hit-test in the object's own space, where the geometry is simple.</summary>
    protected abstract bool HitTestLocal(Point local, double tolerance);

    /// <summary>
    /// This object's own shape, as a value that can be put back later.
    ///
    /// <para>Exists because <see cref="Transform"/> carries no scale, so resizing
    /// edits geometry — and undo therefore needs to capture geometry. One pair of
    /// methods per type keeps that to a single <c>Reshape</c> command rather than
    /// one command per shape.</para>
    ///
    /// <para>The state is a value, not a reference into the object: it is held by
    /// an undo entry that may outlive several edits.</para>
    /// </summary>
    public abstract GeometryState CaptureGeometry();

    public abstract void RestoreGeometry(GeometryState state);

    /// <summary>
    /// Whether an image-space point lands on this object.
    ///
    /// <para>The point is pulled back through the <b>inverse</b> transform rather
    /// than the object being pushed forward, because a rotated shape has no
    /// axis-aligned description in image space.</para>
    ///
    /// <para>Two wrong versions of this are easy to write and both are covered by a
    /// single probe point in <c>HitTestTests</c>: testing <see cref="Bounds"/>
    /// instead, and applying <see cref="Transform"/> instead of its inverse. Each
    /// looks like the app selecting something the user did not click.</para>
    /// </summary>
    public bool HitTest(Point imagePoint, double tolerance)
    {
        var inverse = Transform;
        // Degenerate only if something put a zero scale in here, which the invariant
        // above forbids. Refusing the hit beats throwing under the mouse.
        if (!inverse.HasInverse) return false;
        inverse.Invert();

        return HitTestLocal(inverse.Transform(imagePoint), tolerance);
    }

    public abstract void Render(DrawingContext dc);

    /// <summary>Frozen, so one instance is safe on the canvas thread and the flattener's.</summary>
    protected Brush? FillBrush()
    {
        if (Style.Fill is not { } fill) return null;
        var brush = new SolidColorBrush(fill) { Opacity = Style.Opacity };
        brush.Freeze();
        return brush;
    }

    protected Pen? StrokePen()
    {
        if (Style.StrokeWidth <= 0) return null;
        var brush = new SolidColorBrush(Style.Stroke) { Opacity = Style.Opacity };
        brush.Freeze();
        var pen = new Pen(brush, Style.StrokeWidth);
        pen.Freeze();
        return pen;
    }
}
