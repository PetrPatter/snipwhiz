using System.Windows.Media;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// A marker pass over the picture: a translucent filled rectangle.
///
/// <para><b>It carries no behaviour a rectangle does not already have</b>, and that
/// is deliberate rather than unfinished. It exists for the one thing that cannot be
/// added later — a type tag in the file. Highlights saved as plain rectangles are
/// plain rectangles forever, so if a phase after this gives highlights their own
/// blend, their own "always draw beneath everything else", or their own entry in
/// the style pill's remembered defaults, every highlight drawn before that point is
/// already unreachable. Ten lines now buys a door that costs a format migration
/// later.</para>
///
/// <para><b>Departure from spec §4.5, which called for a multiply blend.</b>
/// <c>DrawingContext</c> has no blend modes. Getting one means an
/// <c>Effect</c>/<c>BitmapEffect</c>, which the flattener does not apply — that is
/// a second render path, the single failure §1 and the WYSIWYG gate exist to
/// prevent, and it would trade a real guarantee for a slightly nicer yellow.
/// Translucent fill is what the tool is for and what the gate can prove.</para>
/// </summary>
public sealed class HighlightAnnotation : RectangleAnnotation
{
    /// <summary>Marker yellow, no outline — a highlighter has no edge.</summary>
    public static readonly AnnotationStyle DefaultStyle = new()
    {
        Fill = Color.FromRgb(0xFF, 0xE0, 0x2B),
        StrokeWidth = 0,
        Opacity = 0.35,
    };

    public HighlightAnnotation() => Style = DefaultStyle;
}
