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
    private static readonly Pen Outline = Frozen(new Pen(Frozen(Color.FromRgb(0xE5, 0x48, 0x4D)), 1.25));
    private static readonly Pen RotateArm = Frozen(new Pen(Frozen(Color.FromRgb(0xE5, 0x48, 0x4D)), 1));
    private static readonly Pen MarqueeEdge = Frozen(new Pen(Frozen(Color.FromRgb(0xE5, 0x48, 0x4D)), 1));
    private static readonly Brush MarqueeFill = Frozen(Color.FromArgb(0x22, 0xE5, 0x48, 0x4D));

    public static void Render(
        DrawingContext dc, CanvasHost canvas, IReadOnlyList<Annotation> selection, Rect? marquee)
    {
        foreach (var annotation in selection) DrawOutline(dc, canvas, annotation);

        // Handles only for a single selection. With several selected the useful
        // gesture is move, and eight handles per object is noise that hides it.
        // Multi-select resize and rotate arrive with the style system in phase E.
        if (selection.Count == 1) DrawHandles(dc, canvas, selection[0]);

        if (marquee is { } band) dc.DrawRectangle(MarqueeFill, MarqueeEdge, band);
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

    /// <summary>
    /// Which handle is under an element-space point, or <see cref="HandleKind.None"/>.
    ///
    /// <para>Tested in element space against every handle's drawn position, so a
    /// rotated object's handles are grabbed exactly where they appear — the
    /// rotation is already baked into where they were drawn.</para>
    /// </summary>
    public static HandleKind HandleAt(CanvasHost canvas, Annotation annotation, Point element)
    {
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
