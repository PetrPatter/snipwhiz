using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
    /// Whether <see cref="AnnotationStyle.Fill"/> is this object's <i>body</i> or the
    /// <i>backdrop behind</i> it. False for every shape; true for text.
    ///
    /// <para>Exists because "make this thing red" is one gesture with two meanings.
    /// On a rectangle it means the rectangle; on text it means the words, and
    /// painting the plate to match makes the caption vanish — which is exactly what
    /// shipped, because the rule "set fill wherever there is one" had no
    /// counter-example when it was written.</para>
    ///
    /// <para>Asked of the type rather than switched on by the toolbar, the same way
    /// <see cref="GeometryForBounds"/> asks each shape what its bounds mean. §4.4
    /// rejects a type switch per control, not a type answering for itself.</para>
    /// </summary>
    protected virtual bool FillIsBackdrop => false;

    /// <summary>
    /// This object's style made a given colour — whatever "this colour" means for
    /// it. Stroke always; body fill too, but never a backdrop.
    ///
    /// <para>Here rather than in the toolbar so there is one answer per type instead
    /// of one rule in the one place that happens to recolour things today.</para>
    /// </summary>
    public AnnotationStyle Recoloured(Color colour) => Style with
    {
        Stroke = colour,
        Fill = Style.Fill is null || FillIsBackdrop ? Style.Fill : colour,
    };

    /// <summary>
    /// The one number the style pill's size control edits.
    ///
    /// <para>Stroke width for a shape, font size for text. Named for the control
    /// rather than for either meaning, because the control has one job and it is the
    /// object that decides what that does to it — the same answer as
    /// <see cref="Recoloured"/> and <see cref="GeometryForBounds"/>.</para>
    ///
    /// <para>Note that for a shape this lives in <see cref="Style"/> and for text it
    /// lives in geometry. Callers do not need to know which: set it, then look at
    /// what moved.</para>
    /// </summary>
    public virtual double SizeControl
    {
        get => Style.StrokeWidth;
        set => Style = Style with { StrokeWidth = value };
    }

    /// <summary>
    /// Whether placing one of these leaves the tool active.
    ///
    /// <para>False for everything that is drawn once and then adjusted; true for step
    /// numbers, where the point is to place several in a row. Asked of the type for
    /// the same reason as everything else here.</para>
    /// </summary>
    public virtual bool PlacesRepeatedly => false;

    public virtual (double Min, double Max) SizeControlRange => (0, 24);

    public virtual string SizeControlLabel => "Stroke width";

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
    /// The geometry this object would have if its bounds were <paramref name="size"/>.
    ///
    /// <para>Resizing works in bounds because that is what handles describe, but
    /// only a box-shaped annotation stores a size. A line stores a vector, and it
    /// has to keep its direction while its extent changes. Without this the
    /// selection tool has to know every shape's geometry type, and it did — it
    /// built a rectangle's state unconditionally, which would have thrown the first
    /// time anyone resized an ellipse.</para>
    /// </summary>
    public abstract GeometryState GeometryForBounds(Size size);

    /// <summary>
    /// Grab points this object has beyond the eight resizers and the rotate handle.
    ///
    /// <para>Empty for everything with a box and a rotation, which was every
    /// annotation until the callout tail. The tail is neither: dragging it does not
    /// resize the bubble and does not turn it, it re-aims the thing the bubble is
    /// pointing at.</para>
    ///
    /// <para>Three small members rather than teaching <see cref="Handles"/> about
    /// callouts, for the same reason <see cref="GeometryForBounds"/> exists: the
    /// selection tool would otherwise have to know which types have what, and B1
    /// already showed what that costs.</para>
    /// </summary>
    public virtual IReadOnlyList<HandleKind> ControlPoints => [];

    /// <summary>
    /// Where one of <see cref="ControlPoints"/> sits, in this object's own space.
    ///
    /// <para>The origin by default, which is the centre — <see cref="LocalBounds"/>
    /// is centred on the origin for every annotation, so this is exactly what
    /// <see cref="Handles.LocalPosition"/> already fell through to for a kind it did
    /// not recognise.</para>
    /// </summary>
    public virtual Point ControlPoint(HandleKind kind) => new(0, 0);

    /// <summary>
    /// The geometry this object would have with a control point dragged to a local
    /// point, or null if it has no such point.
    /// </summary>
    public virtual GeometryState? MoveControlPoint(HandleKind kind, Point local) => null;

    /// <summary>
    /// Shapes this object to span two image-space points, unrotated.
    ///
    /// <para>What a create-by-drag gesture means, per shape: a box for a rectangle,
    /// a vector for a line. Having each type answer it is what lets one tool draw
    /// all of them.</para>
    /// </summary>
    public abstract void Fit(Point from, Point to);

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

    /// <summary>
    /// Draws this object.
    ///
    /// <para><paramref name="source"/> is the <b>original capture</b>, and every
    /// annotation is handed it whether or not it wants it. Most do not: a rectangle
    /// is a rectangle. Blur, pixelate and magnify are not pure vector objects — they
    /// have to look at pixels — and the alternative to one signature that can express
    /// that is a second render entry point for the ones that sample, which is the
    /// exact shape of the thing §1 and the WYSIWYG gate exist to prevent.</para>
    ///
    /// <para><b>The original, not the composite</b> (§4.9). Sampling what is beneath
    /// would make paint order load-bearing, force a re-render of every pixel tool
    /// whenever anything below it changed, and cycle when two blurs overlap. The
    /// documented consequence is that an arrow underneath a blur is not blurred —
    /// correct for a redaction tool, because the thing being hidden is in the
    /// capture.</para>
    /// </summary>
    public abstract void Render(DrawingContext dc, BitmapSource source);

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
