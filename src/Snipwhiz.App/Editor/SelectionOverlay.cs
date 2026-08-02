using System.Windows;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;

namespace Snipwhiz.App.Editor;

/// <summary>
/// Draws the selection — outline, grab handles, rotate handle, marquee — and says
/// which handle is under a point.
///
/// <para>Not a WPF <c>Adorner</c>, despite doing an adorner's job. It renders into a
/// visual the canvas already owns, which avoids standing up an adorner layer and
/// keeps the whole selection on the same untransformed layer as the marquee.</para>
///
/// <para>Everything here is in <b>element</b> coordinates: handles are perceptual
/// and must not scale with the image.</para>
/// </summary>
internal static class SelectionOverlay
{
    /// <summary>Side of a grab handle, on screen.</summary>
    public const double HandleSize = 9;

    /// <summary>How far the rotate handle sits above the object, on screen.</summary>
    public const double RotateGap = 26;

    /// <summary>Generous on purpose: a 9px square is hard to hit exactly.</summary>
    public const double GrabTolerance = 7;

    private static readonly Brush HandleFill = Frozen(Color.FromRgb(0xFA, 0xFA, 0xFA));
    private static readonly Pen HandleEdge = Frozen(new Pen(Frozen(Color.FromRgb(0x1C, 0x1B, 0x1A)), 1));
    private static readonly Pen Outline = Frozen(new Pen(Frozen(Color.FromRgb(0xE8, 0x83, 0x3A)), 1.25));
    private static readonly Pen RotateArm = Frozen(new Pen(Frozen(Color.FromRgb(0xE8, 0x83, 0x3A)), 1));
    private static readonly Pen MarqueeEdge = Frozen(new Pen(Frozen(Color.FromRgb(0xE8, 0x83, 0x3A)), 1));
    private static readonly Brush MarqueeFill = Frozen(Color.FromArgb(0x22, 0xE8, 0x83, 0x3A));

    private static readonly Brush CropDim = Frozen(Color.FromArgb(0xA8, 0x0E, 0x0D, 0x0C));

    /// <summary>
    /// The marquee: the four corner colours of the app's icon, clockwise from the
    /// top left. The same four the library tile uses.
    /// </summary>
    private static readonly Pen[] MarqueeCorners =
    [
        Frozen(new Pen(Frozen(Color.FromRgb(0xFF, 0xA1, 0x2E)), 2.5)),
        Frozen(new Pen(Frozen(Color.FromRgb(0xFF, 0x3D, 0x6E)), 2.5)),
        Frozen(new Pen(Frozen(Color.FromRgb(0xA4, 0x6B, 0xFF)), 2.5)),
        Frozen(new Pen(Frozen(Color.FromRgb(0x1F, 0xE0, 0xB4)), 2.5)),
    ];

    /// <summary>
    /// The crop rectangle's own edge, joining the four corner brackets.
    ///
    /// <para>Deliberately dimmer than the brackets and than the old accent outline it
    /// replaces: it is there to close the shape, not to compete with the corners for
    /// which part of this you are meant to look at.</para>
    /// </summary>
    private static readonly Pen CropEdge = Frozen(new Pen(Frozen(Color.FromArgb(0x66, 0xF5, 0xF2, 0xEC)), 1));

    /// <summary>How far a marquee bracket runs along each edge, on screen.</summary>
    private const double CornerArm = 22;

    private static readonly Brush ControlFill = Frozen(Color.FromRgb(0xE8, 0x83, 0x3A));

    public static void Render(
        DrawingContext dc, CanvasHost canvas, IReadOnlyList<Annotation> selection, Rect? marquee)
    {
        // Before the selection, so a selected object inside the crop still reads as
        // selected while the area around it is dimmed.
        if (canvas.CropPreview is { } crop) DrawCrop(dc, canvas, crop);

        // A line has no box, so it gets no outline. Its two ends are drawn below as
        // control points and they are the whole of its selection.
        foreach (var annotation in selection)
        {
            if (annotation.HasBoundingBox) DrawOutline(dc, canvas, annotation);
        }

        // Handles only for a single selection. With several selected the useful
        // gesture is move, and eight handles per object is noise that hides it.
        // Multi-select resize and rotate arrive with the style system in phase E.
        if (selection.Count == 1) DrawHandles(dc, canvas, selection[0]);

        if (marquee is { } band) dc.DrawRectangle(MarqueeFill, MarqueeEdge, band);
    }

    /// <summary>
    /// Dims everything outside the pending crop and puts handles on its edge.
    ///
    /// <para>The dim is one even-odd geometry rather than four rectangles round the
    /// edges: four rectangles have to be computed for a rect that may extend past
    /// the viewport in any direction, and the arithmetic for the corners is exactly
    /// the sort that is wrong only when the crop is partly off-screen.</para>
    /// </summary>
    private static void DrawCrop(DrawingContext dc, CanvasHost canvas, Rect crop)
    {
        var inner = new Rect(canvas.ToElement(crop.TopLeft), canvas.ToElement(crop.BottomRight));
        var outer = new Rect(0, 0, canvas.ActualWidth, canvas.ActualHeight);

        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry(outer));
        mask.Children.Add(new RectangleGeometry(inner));
        mask.Freeze();
        dc.DrawGeometry(CropDim, null, mask);

        // The full rectangle, faintly, under the corners. The corners alone say where
        // the crop's four corners are and leave you to infer the edges between them —
        // which on a large crop is a long way to infer. The brackets are the accent;
        // this is the shape.
        dc.DrawRectangle(null, CropEdge, inner);

        DrawMarquee(dc, inner);

        // The same handle geometry the selection uses, positioned from the same
        // Handles table — a crop rectangle is an unrotated box and there is no second
        // implementation of where its corners are.
        var proxy = CropProxy.For(crop);
        foreach (var kind in Handles.Resizers)
        {
            var centre = canvas.ToElement(Handles.ImagePosition(proxy, kind, 0));
            dc.DrawRectangle(HandleFill, HandleEdge, new Rect(
                centre.X - HandleSize / 2, centre.Y - HandleSize / 2, HandleSize, HandleSize));
        }
    }

    /// <summary>
    /// Four corner brackets in the icon's colours — the same mark the library tile
    /// wears, and the same one the capture overlay draws round a region.
    ///
    /// <para><b>Regions get the marquee; objects do not.</b> The plan asked for this
    /// on a selected annotation too, and that is wrong: the marquee means <i>an area
    /// is chosen</i>, which is true of a crop and of a tile and is not true of a
    /// rectangle you drew. An object already says what it is by having eight resize
    /// handles and a rotate arm, and putting brackets over those would be two
    /// selection languages on one shape. So an annotation keeps its accent outline,
    /// and orange still means "the thing you are acting on".</para>
    ///
    /// <para>Brackets are clamped to half the rectangle so a crop dragged down to a
    /// few pixels shows four short corners rather than four overlapping ones.</para>
    /// </summary>
    private static void DrawMarquee(DrawingContext dc, Rect box)
    {
        var arm = Math.Min(CornerArm, Math.Min(box.Width, box.Height) / 2);
        if (arm <= 0) return;

        // Clockwise from the top left, matching the pen order.
        var corners = new[]
        {
            (Point: box.TopLeft,     X: 1.0,  Y: 1.0),
            (Point: box.TopRight,    X: -1.0, Y: 1.0),
            (Point: box.BottomRight, X: -1.0, Y: -1.0),
            (Point: box.BottomLeft,  X: 1.0,  Y: -1.0),
        };

        for (var i = 0; i < corners.Length; i++)
        {
            var (point, dx, dy) = corners[i];
            var pen = MarqueeCorners[i];
            dc.DrawLine(pen, point, new Point(point.X + arm * dx, point.Y));
            dc.DrawLine(pen, point, new Point(point.X, point.Y + arm * dy));
        }
    }

    private static void DrawOutline(DrawingContext dc, CanvasHost canvas, Annotation annotation)
    {
        var figure = new PathFigure
        {
            StartPoint = Corner(canvas, annotation, HandleKind.TopLeft),
            IsClosed = true,
        };
        figure.Segments.Add(new PolyLineSegment(
        [
            Corner(canvas, annotation, HandleKind.TopRight),
            Corner(canvas, annotation, HandleKind.BottomRight),
            Corner(canvas, annotation, HandleKind.BottomLeft),
        ], true));

        var geometry = new PathGeometry([figure]);
        geometry.Freeze();
        dc.DrawGeometry(null, Outline, geometry);
    }

    private static void DrawHandles(DrawingContext dc, CanvasHost canvas, Annotation annotation)
    {
        if (annotation.HasBoundingBox)
        {
            var top = Position(canvas, annotation, HandleKind.Top);
            var rotate = Position(canvas, annotation, HandleKind.Rotate);
            dc.DrawLine(RotateArm, top, rotate);
            dc.DrawEllipse(HandleFill, HandleEdge, rotate, HandleSize / 2, HandleSize / 2);

            foreach (var kind in Handles.Resizers)
            {
                var centre = Position(canvas, annotation, kind);
                dc.DrawRectangle(HandleFill, HandleEdge, new Rect(
                    centre.X - HandleSize / 2, centre.Y - HandleSize / 2, HandleSize, HandleSize));
            }
        }

        // Round and accent-filled, so a control point does not read as a ninth
        // resize handle. Dragging it does something else entirely.
        foreach (var kind in annotation.ControlPoints)
        {
            var centre = Position(canvas, annotation, kind);
            dc.DrawEllipse(ControlFill, HandleEdge, centre, HandleSize / 2, HandleSize / 2);
        }
    }

    /// <summary>
    /// Which handle is under an element-space point, or <see cref="HandleKind.None"/>.
    ///
    /// <para>Tested in element space against every handle's drawn position, so a
    /// rotated object's handles are grabbed exactly where they appear — the
    /// rotation is already baked into where they were drawn.</para>
    /// </summary>
    public static HandleKind HandleAt(CanvasHost canvas, Annotation annotation, Point element)
    {
        // Control points first. A callout's tail can be dragged anywhere, including
        // on top of a resize handle, and the one that does the unusual thing should
        // win — a resizer that has been covered can still be reached from the other
        // side of the object.
        foreach (var kind in annotation.ControlPoints)
        {
            if (Near(Position(canvas, annotation, kind), element)) return kind;
        }

        // No box means no resizers and no rotate arm to grab — and they must not be
        // grabbable while invisible, or a line has an eight-handle hit region drawn
        // nowhere.
        if (!annotation.HasBoundingBox) return HandleKind.None;

        if (Near(Position(canvas, annotation, HandleKind.Rotate), element)) return HandleKind.Rotate;

        foreach (var kind in Handles.Resizers)
        {
            if (Near(Position(canvas, annotation, kind), element)) return kind;
        }
        return HandleKind.None;
    }

    private static bool Near(Point handle, Point probe) =>
        Math.Abs(handle.X - probe.X) <= GrabTolerance && Math.Abs(handle.Y - probe.Y) <= GrabTolerance;

    private static Point Position(CanvasHost canvas, Annotation annotation, HandleKind kind) =>
        canvas.ToElement(Handles.ImagePosition(annotation, kind, canvas.ToImageLength(RotateGap)));

    private static Point Corner(CanvasHost canvas, Annotation annotation, HandleKind kind) =>
        canvas.ToElement(Handles.ImagePosition(annotation, kind, 0));

    private static SolidColorBrush Frozen(Color colour)
    {
        var brush = new SolidColorBrush(colour);
        brush.Freeze();
        return brush;
    }

    private static Pen Frozen(Pen pen)
    {
        pen.Freeze();
        return pen;
    }
}
