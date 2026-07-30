using System.Windows;
using Snipwhiz.Core.Annotations;

namespace Snipwhiz.App.Tests;

/// <summary>
/// The pill's one slider means different things to different objects, and the wiring
/// between the slider and the object is what this covers.
///
/// <para><b>Regression.</b> The slider was wired to <c>Style.StrokeWidth</c>, which
/// text does not use — so dragging it while a caption was selected moved the number
/// on screen and changed nothing at all. <c>Annotation.SizeControl</c> fixed the
/// rule and is tested in Core; what was never tested is that the slider reaches it,
/// and that the slider's <i>range</i> moves with the selected type. A 0–24 range on
/// a caption lets someone drag it to 0pt, which is a caption that has vanished for
/// the second time.</para>
/// </summary>
public class SizeControlTests
{
    [Fact]
    public void The_slider_sets_stroke_width_on_a_shape()
    {
        Harness.Editor(editor =>
        {
            var shape = new RectangleAnnotation { Size = new Size(40, 30) };
            editor.Canvas.SetSelection([shape]);

            editor.WidthSlider.Value = 11;

            Assert.Equal(11, shape.Style.StrokeWidth);
        });
    }

    [Fact]
    public void The_slider_sets_font_size_on_a_caption_and_leaves_the_stroke_alone()
    {
        Harness.Editor(editor =>
        {
            var text = new TextAnnotation { Text = "hello" };
            var stroke = text.Style.StrokeWidth;
            editor.Canvas.SetSelection([text]);

            editor.WidthSlider.Value = 40;

            Assert.Equal(40, text.FontSize);
            Assert.Equal(stroke, text.Style.StrokeWidth);
        });
    }

    [Fact]
    public void The_range_follows_the_selected_type()
    {
        Harness.Editor(editor =>
        {
            editor.Canvas.SetSelection([new RectangleAnnotation { Size = new Size(40, 30) }]);
            Assert.Equal((0, 24), (editor.WidthSlider.Minimum, editor.WidthSlider.Maximum));

            editor.Canvas.SetSelection([new TextAnnotation { Text = "hello" }]);
            Assert.Equal((8, 120), (editor.WidthSlider.Minimum, editor.WidthSlider.Maximum));
        });
    }

    /// <summary>
    /// Selecting a caption larger than the previous selection's range must not
    /// shrink it.
    ///
    /// <para>This is the test that carries the <c>_syncingPill</c> guard, and the
    /// only one that can. Writing the slider from the selection normally echoes back
    /// a value the object already has, which is a no-op whether the guard is there
    /// or not — so "selecting an object does not change it" is untriggerable and was
    /// dropped from this file rather than kept as a test that passes for no
    /// reason.</para>
    ///
    /// <para>What is <i>not</i> a no-op is WPF coercing the value into a range that
    /// has not moved yet: the slider is still on a shape's 0–24 when the caption
    /// arrives at 96pt, and the echo would write 24 back onto it. Removing the guard
    /// fails exactly this, and nothing else.</para>
    /// </summary>
    [Fact]
    public void Selecting_a_large_caption_does_not_shrink_it()
    {
        Harness.Editor(editor =>
        {
            // Select a shape first, so the slider is genuinely sitting on the 0-24
            // range when the caption arrives.
            editor.Canvas.SetSelection([new RectangleAnnotation { Size = new Size(40, 30) }]);

            var text = new TextAnnotation { Text = "hello", FontSize = 96 };
            editor.Canvas.SetSelection([text]);

            Assert.Equal(96, text.FontSize);
        });
    }

    // NOT COVERED HERE: what recolouring means for each type. Annotation.Recoloured
    // and FillIsBackdrop are Core's rule and RecolourTests covers them, including
    // the caption plated in its own ink. The App side is one lambda handing the
    // colour to the object, and a test of it would be a test of Core with extra
    // steps.
}
