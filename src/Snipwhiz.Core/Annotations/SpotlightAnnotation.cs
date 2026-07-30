using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// Draws attention to a region by dimming everything else.
///
/// <para>The inverse of every other tool here: it is the only one whose subject is
/// the part it does <b>not</b> paint.</para>
///
/// <para><b>No departure from §4.5 was needed</b>, and the spec said so in advance
/// while recording highlight's. A rectangle with a hole in it is one even-odd
/// geometry, which <see cref="DrawingContext"/> draws directly — so unlike
/// highlight's multiply blend there is nothing here that would have needed a WPF
/// <c>Effect</c>, and therefore nothing that would render on screen and not in the
/// export.</para>
///
/// <para>It samples no pixels despite being a §4.5 emphasis tool, so the capture it
/// is handed is used only for its <i>size</i> — the dim has to reach the edges of
/// the picture, and before this signature existed an annotation had no way to know
/// where those were.</para>
/// </summary>
public sealed class SpotlightAnnotation : RectangleAnnotation
{
    /// <summary>
    /// Dark enough to push the surroundings back, light enough to leave them
    /// readable — a spotlight that blacks out the rest is a crop with extra steps.
    /// </summary>
    public static readonly AnnotationStyle DefaultStyle = new()
    {
        Stroke = Colors.Black,
        StrokeWidth = 0,
        Fill = Colors.Black,
        Opacity = 0.55,
    };

    public SpotlightAnnotation() => Style = DefaultStyle;

    /// <summary>
    /// The pill's size control is how strong the dim is, as a percentage.
    ///
    /// <para>Percent rather than the 0–1 the style stores, because the slider snaps
    /// to whole numbers and shows the value without decimals — a 0–1 range would give
    /// a control with two usable positions.</para>
    /// </summary>
    public override double SizeControl
    {
        get => Math.Round(Style.Opacity * 100);
        set => Style = Style with { Opacity = value / 100 };
    }

    /// <summary>
    /// Never 0 and never 100: a spotlight at neither end is an invisible object that
    /// still hit-tests, which reads as the app having put something there by
    /// accident.
    /// </summary>
    public override (double Min, double Max) SizeControlRange => (10, 95);

    public override string SizeControlLabel => "Dim";

    /// <summary>
    /// The lit region is a hole, so clicking inside it must not select the
    /// spotlight — the object there is whatever is being spotlit.
    ///
    /// <para>The rest of the capture is the object, but hit-testing all of it would
    /// mean a spotlight swallows every click anywhere else. The compromise is the
    /// frame: a grab band just outside the lit edge, which is where a hand reaches
    /// to move or resize one anyway.</para>
    /// </summary>
    protected override bool HitTestLocal(Point local, double tolerance)
    {
        var inner = LocalBounds;
        var outer = inner;
        outer.Inflate(tolerance + GrabBand, tolerance + GrabBand);
        inner.Inflate(-tolerance, -tolerance);

        return outer.Contains(local) && !inner.Contains(local);
    }

    /// <summary>How far outside the lit edge counts as grabbing the spotlight.</summary>
    private const double GrabBand = 6;

    public override void Render(DrawingContext dc, BitmapSource source)
    {
        var brush = FillBrush();
        if (brush is null) return;

        // Even-odd, so the lit rectangle punches a hole rather than being painted
        // over. Four rectangles round the edges is the other way to write this and
        // it is four chances to get a corner wrong, only visible when the lit region
        // reaches past the picture.
        var mask = new GeometryGroup { FillRule = FillRule.EvenOdd };
        mask.Children.Add(new RectangleGeometry(
            new Rect(0, 0, source.PixelWidth, source.PixelHeight)));
        mask.Children.Add(new RectangleGeometry(LocalBounds)
        {
            // Carried on the geometry rather than pushed on the context: the outer
            // rectangle is in image space and the inner one is not, and they have to
            // end up in the same geometry to be one hole.
            Transform = new MatrixTransform(Transform),
        });
        mask.Freeze();

        dc.DrawGeometry(brush, null, mask);
    }
}
