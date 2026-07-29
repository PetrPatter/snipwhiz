using System.Windows;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;
using Xunit;

namespace Snipwhiz.Core.Tests.Annotations;

/// <summary>
/// What "make this thing this colour" means, per type.
///
/// <para>Written after the style pill painted a caption's plate the same colour as
/// its words and the text disappeared. The rule it used — set the fill wherever
/// there is one — was reasonable against the four shapes that existed when it was
/// written, and wrong the moment a type arrived whose fill is a backdrop rather
/// than a body.</para>
/// </summary>
public class RecolourTests
{
    private static readonly Color Red = Color.FromRgb(0xE5, 0x48, 0x4D);

    [Fact]
    public void Recolouring_text_never_makes_its_plate_match_its_ink()
    {
        // THE test. Equal ink and plate is not a bad-looking caption, it is an
        // invisible one, and nothing downstream can recover it.
        var text = new TextAnnotation { Text = "caption" };

        var restyled = text.Recoloured(Red);

        Assert.Equal(Red, restyled.Stroke);
        Assert.Equal(TextAnnotation.DefaultStyle.Fill, restyled.Fill);
        Assert.NotEqual(restyled.Fill, restyled.Stroke);
    }

    [Fact]
    public void Recolouring_a_filled_shape_changes_both_its_outline_and_its_body()
    {
        // The contrast that makes the text case meaningful: same call, and here the
        // fill is the object, so it does change.
        var rectangle = new RectangleAnnotation
        {
            Size = new Size(100, 50),
            Style = AnnotationStyle.Default with { Fill = Colors.Teal },
        };

        var restyled = rectangle.Recoloured(Red);

        Assert.Equal(Red, restyled.Stroke);
        Assert.Equal(Red, restyled.Fill);
    }

    [Fact]
    public void Recolouring_an_unfilled_shape_leaves_it_unfilled()
    {
        var rectangle = new RectangleAnnotation { Size = new Size(100, 50) };

        Assert.Null(rectangle.Recoloured(Red).Fill);
    }

    [Fact]
    public void Recolouring_a_highlight_changes_the_wash_you_can_actually_see()
    {
        // A highlight has no stroke, so recolouring only its stroke would look like
        // the swatch doing nothing at all.
        var highlight = new HighlightAnnotation();

        Assert.Equal(Red, highlight.Recoloured(Red).Fill);
    }

    [Fact]
    public void Recolouring_leaves_everything_that_is_not_a_colour_alone()
    {
        var line = new LineAnnotation
        {
            Style = AnnotationStyle.Default with { StrokeWidth = 9, Opacity = 0.5 },
        };

        var restyled = line.Recoloured(Red);

        Assert.Equal(9, restyled.StrokeWidth);
        Assert.Equal(0.5, restyled.Opacity);
    }
}
