using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// A numbered badge, for walking someone through a screenshot in order.
///
/// <para><b>The number is not stored.</b> It is this object's position among the
/// step annotations in the document, stamped on by
/// <see cref="Scene.SceneDocument.NumberSteps"/> before anything draws. Deleting
/// step 2 of 5 therefore closes the gap — which is what a reader expects of a
/// numbered list, and a screenshot that runs 1, 3, 4, 5 reads as a mistake by
/// whoever receives it.</para>
///
/// <para>Storing nothing also means <b>undo needs no counter to roll back</b>.
/// Delete is a delete; <see cref="Scene.RemoveAnnotation"/> already restores the
/// object's position in the list, so the numbers come back correct on their own. A
/// stored number would have needed every command that adds, removes or reorders a
/// step to renumber the rest, and the one that forgot would be found by a user.</para>
/// </summary>
public sealed class StepAnnotation : Annotation
{
    public const double DefaultDiameter = 34;

    /// <summary>Below this a two-digit number does not fit inside the circle.</summary>
    public const double MinDiameter = 18;

    private static readonly Typeface Face = new(
        new FontFamily("Segoe UI"), FontStyles.Normal, FontWeights.SemiBold, FontStretches.Normal);

    public static readonly AnnotationStyle DefaultStyle = new()
    {
        Stroke = Colors.White,
        StrokeWidth = 0,
        Fill = Color.FromRgb(0xE5, 0x48, 0x4D),
    };

    public double Diameter { get; set; } = DefaultDiameter;

    /// <summary>
    /// What this badge currently reads, one-based.
    ///
    /// <para>Written by the document, never by a tool and never by a file. It exists
    /// as a field only because <see cref="Render"/> has to draw a digit and has no
    /// way to ask where in the list it sits.</para>
    /// </summary>
    public int Number { get; set; } = 1;

    public StepAnnotation() => Style = DefaultStyle;

    /// <summary>
    /// Placing one leaves the tool active, so the common case is click, click, click.
    ///
    /// <para>Asked of the type rather than switched on by the toolbar, like
    /// <see cref="Annotation.SizeControl"/> and <see cref="GeometryForBounds"/>.</para>
    /// </summary>
    public override bool PlacesRepeatedly => true;

    public override Rect LocalBounds =>
        new(-Diameter / 2, -Diameter / 2, Diameter, Diameter);

    /// <summary>
    /// A badge has one size, set from the pill, so a drag positions it rather than
    /// sizing it — and a plain click places one, which is the whole point of the
    /// tool.
    /// </summary>
    public override void Fit(Point from, Point to) =>
        Transform = new Matrix(1, 0, 0, 1, to.X, to.Y);

    public override double SizeControl
    {
        get => Diameter;
        set => Diameter = Math.Max(MinDiameter, value);
    }

    public override (double Min, double Max) SizeControlRange => (MinDiameter, 96);

    public override string SizeControlLabel => "Badge size";

    public override GeometryState CaptureGeometry() => new StepGeometryState(Diameter);

    public override void RestoreGeometry(GeometryState state) =>
        Diameter = ((StepGeometryState)state).Diameter;

    /// <summary>Stays a circle however the handles are dragged.</summary>
    public override GeometryState GeometryForBounds(Size size) =>
        new StepGeometryState(Math.Max(MinDiameter, Math.Min(size.Width, size.Height)));

    protected override bool HitTestLocal(Point local, double tolerance)
    {
        var reach = Diameter / 2 + tolerance;
        return local.X * local.X + local.Y * local.Y <= reach * reach;
    }

    public override void Render(DrawingContext dc, BitmapSource source)
    {
        var fill = FillBrush();
        if (fill is null) return;

        dc.PushTransform(new MatrixTransform(Transform));

        dc.DrawEllipse(fill, StrokePen(), new Point(0, 0), Diameter / 2, Diameter / 2);

        var text = Digits();
        dc.DrawText(text, new Point(-text.Width / 2, -text.Height / 2));

        dc.Pop();
    }

    private FormattedText Digits() => new(
        Number.ToString(CultureInfo.InvariantCulture),
        CultureInfo.InvariantCulture,
        System.Windows.FlowDirection.LeftToRight,
        Face,
        // Proportional to the badge, so resizing does not leave a large circle with
        // a small number rattling around inside it.
        Diameter * 0.54,
        Ink(),
        pixelsPerDip: 1.0)
    {
        TextAlignment = TextAlignment.Center,
    };

    /// <summary>
    /// The digit's colour, <b>derived from the badge rather than stored</b>.
    ///
    /// <para>A step badge is its fill — recolouring one means recolouring the circle
    /// — so <see cref="Annotation.Recoloured"/> sets stroke and fill together, and a
    /// digit painted in the stroke colour would vanish into it. That is exactly what
    /// happened to text's plate, and the fix there was to say which of the two is the
    /// backdrop. Here neither is: the number is not a colour anyone should have to
    /// choose, so it is whichever of white or near-black stays readable.</para>
    /// </summary>
    private SolidColorBrush Ink()
    {
        var badge = Style.Fill ?? Colors.Black;
        var luminance = 0.299 * badge.R + 0.587 * badge.G + 0.114 * badge.B;

        var brush = new SolidColorBrush(luminance > 140
            ? Color.FromRgb(0x1C, 0x1B, 0x1A)
            : Colors.White)
        {
            Opacity = Style.Opacity,
        };
        brush.Freeze();
        return brush;
    }
}
