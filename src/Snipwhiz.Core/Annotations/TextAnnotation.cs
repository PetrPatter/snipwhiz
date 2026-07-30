using System.Globalization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// A run of text on a plate.
///
/// <para><b>Its size is measured, not stored.</b> Every other annotation is told how
/// big it is; this one is told what it says, and the bounds fall out of the font.
/// That is what makes the plate fit the words instead of the words being clipped by
/// a plate someone dragged.</para>
/// </summary>
/// <para>Not sealed: <see cref="CalloutAnnotation"/> is this with a tail, and reuses
/// the measuring, the editing overlay and the metrics seam §4.8 was so careful
/// about.</para>
public class TextAnnotation : Annotation
{
    public const string FontFamilyName = "Segoe UI";

    /// <summary>Space between the words and the edge of the plate, in image pixels.</summary>
    public const double Padding = 8;

    public const double CornerRadius = 6;

    /// <summary>
    /// <b>Ideal, never Display</b>, and the editing <c>TextBox</c> must agree.
    ///
    /// <para>Display mode snaps glyphs to whole pixels as it lays them out. The
    /// canvas lays this text out once in image space and then scales the whole scene
    /// by the zoom, so snapped positions would be scaled <i>after</i> snapping and
    /// land somewhere the editor's own layout never puts them. Ideal keeps layout
    /// linear under scale, which is the only reason the two can be made to agree at
    /// all — spec risk 3.</para>
    /// </summary>
    public const TextFormattingMode Formatting = TextFormattingMode.Ideal;

    public static readonly Typeface Face = new(
        new FontFamily(FontFamilyName), FontStyles.Normal, FontWeights.Normal, FontStretches.Normal);

    /// <summary>Ink on a dark plate: legible over a screenshot of anything.</summary>
    public static readonly AnnotationStyle DefaultStyle = new()
    {
        Stroke = Color.FromRgb(0xF5, 0xF2, 0xEC),
        StrokeWidth = 0,
        Fill = Color.FromArgb(0xD8, 0x1C, 0x1B, 0x1A),
        Opacity = 1,
    };

    public TextAnnotation() => Style = DefaultStyle;

    /// <summary>The plate is what the words sit on, not what the object is.</summary>
    protected override bool FillIsBackdrop => true;

    public string Text { get; set; } = "";

    public double FontSize { get; set; } = 28;

    /// <summary>
    /// Font size, not stroke width. Text draws no stroke at all, so the pill's size
    /// control had nothing to move until this said what it meant here.
    /// </summary>
    public override double SizeControl
    {
        get => FontSize;
        set => FontSize = value;
    }

    public override (double Min, double Max) SizeControlRange => (8, 120);

    public override string SizeControlLabel => "Font size";

    /// <summary>
    /// True while an editor is open over this object: it draws its plate but not its
    /// words, so the <c>TextBox</c> supplies the only glyphs on screen.
    ///
    /// <para>Transient view state on a model object, which is a real cost, paid to
    /// keep a single <see cref="Render"/> — the alternative is the overlay drawing
    /// its own plate, which is the same appearance described in two places. Nothing
    /// persists it, and the editor commits before anything saves.</para>
    /// </summary>
    public bool IsBeingEdited { get; set; }

    /// <summary>The words alone, without the plate's padding.</summary>
    public Size TextSize => Measured();

    public override Rect LocalBounds
    {
        get
        {
            var text = Measured();
            var width = text.Width + Padding * 2;
            var height = text.Height + Padding * 2;
            return new Rect(-width / 2, -height / 2, width, height);
        }
    }

    /// <summary>Where the first glyph starts, in this object's own space.</summary>
    public Point TextOrigin
    {
        get
        {
            var bounds = LocalBounds;
            return new Point(bounds.X + Padding, bounds.Y + Padding);
        }
    }

    /// <summary>Places the text centred between the two points; typing decides its size.</summary>
    public override void Fit(Point from, Point to) =>
        Transform = new Matrix(1, 0, 0, 1, (from.X + to.X) / 2, (from.Y + to.Y) / 2);

    public override GeometryState CaptureGeometry() => new TextGeometryState(Text, FontSize);

    public override void RestoreGeometry(GeometryState state)
    {
        var text = (TextGeometryState)state;
        Text = text.Text;
        FontSize = text.FontSize;
    }

    /// <summary>
    /// Resizing changes the <b>font size</b>, not a scale.
    ///
    /// <para>Stretching text is what a shape would do, and it would break the rigid
    /// transform invariant besides. Driven by height rather than width because
    /// height is what a font size means; the width then follows from the glyphs.
    /// Padding is subtracted first, or it would be scaled along with the words and
    /// the plate would creep.</para>
    /// </summary>
    public override GeometryState GeometryForBounds(Size size)
    {
        var current = Measured().Height;
        var wanted = size.Height - Padding * 2;
        if (current <= 0 || wanted <= 0) return CaptureGeometry();

        return new TextGeometryState(Text, Math.Clamp(FontSize * (wanted / current), 6, 400));
    }

    protected override bool HitTestLocal(Point local, double tolerance)
    {
        // The plate is the target, not the glyphs. Requiring a click on a letter
        // would make the gaps between words select whatever is behind.
        var hit = LocalBounds;
        hit.Inflate(tolerance, tolerance);
        return hit.Contains(local);
    }

    public override void Render(DrawingContext dc, BitmapSource source)
    {
        var bounds = LocalBounds;

        dc.PushTransform(new MatrixTransform(Transform));

        // No pen. Stroke is the ink colour here, so stroking the plate with it draws
        // a border the colour of the words — and the style pill's width slider would
        // silently grow it.
        if (FillBrush() is { } plate)
        {
            dc.DrawGeometry(plate, null, Plate(bounds));
        }

        if (!IsBeingEdited)
        {
            var ink = new SolidColorBrush(Style.Stroke) { Opacity = Style.Opacity };
            ink.Freeze();
            dc.DrawText(Format(ink), TextOrigin);
        }

        dc.Pop();
    }

    /// <summary>
    /// The shape drawn behind the words.
    ///
    /// <para>Virtual so a callout can hand back its bubble and tail as one geometry
    /// without a second <see cref="Render"/>. Everything else about drawing text —
    /// the plate brush, the no-pen rule, the skip while editing — stays in one
    /// place, which is the point.</para>
    /// </summary>
    // Qualified: Snipwhiz.Core.Geometry is a namespace in this assembly and wins
    // over the WPF type, the same collision FlowDirection has with WinForms.
    protected virtual System.Windows.Media.Geometry Plate(Rect bounds)
    {
        var plate = new RectangleGeometry(bounds, CornerRadius, CornerRadius);
        plate.Freeze();
        return plate;
    }

    /// <summary>
    /// The one place a <see cref="FormattedText"/> is built, so measuring and drawing
    /// can never disagree about the font, the size or the formatting mode.
    /// </summary>
    public FormattedText Format(Brush brush) => new(
        // A blank string measures zero and the plate collapses to padding, leaving
        // nothing to click and no room for a caret.
        Text.Length == 0 ? " " : Text,
        CultureInfo.CurrentCulture,
        // Qualified: System.Windows.Forms has a FlowDirection too, and Core still
        // references WinForms for the tray icon.
        System.Windows.FlowDirection.LeftToRight,
        Face,
        FontSize,
        brush,
        numberSubstitution: null,
        Formatting,
        // One, because this is laid out in image pixels and the canvas applies the
        // zoom afterwards. Baking a device scale in here would mean the same
        // annotation measured differently on two monitors.
        pixelsPerDip: 1.0);

    private string? _measuredText;
    private double _measuredSize;
    private Size _measured;

    /// <summary>
    /// Cached: <see cref="LocalBounds"/> is read by hit-testing, handles and the
    /// selection outline, and laying the text out afresh each time would put a font
    /// measurement inside mouse-move.
    /// </summary>
    private Size Measured()
    {
        if (_measuredText != Text || _measuredSize != FontSize)
        {
            var formatted = Format(Brushes.Black);
            _measured = new Size(formatted.WidthIncludingTrailingWhitespace, formatted.Height);
            _measuredText = Text;
            _measuredSize = FontSize;
        }
        return _measured;
    }
}
