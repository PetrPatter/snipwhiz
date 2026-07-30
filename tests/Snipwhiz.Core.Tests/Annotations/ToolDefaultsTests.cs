using System.Windows.Media;
using Snipwhiz.Core;
using Snipwhiz.Core.Annotations;
using Xunit;

namespace Snipwhiz.Core.Tests.Annotations;

/// <summary>
/// The style pill's memory: what was last used with a tool is what it draws with
/// next. Written after a report that a new shape inherited the colour but not the
/// width, which is the shape of bug that hides when only one property is asserted.
/// </summary>
public class ToolDefaultsTests
{
    private static ToolDefaults Defaults(Settings settings) =>
        new(settings, Path.Combine(Path.GetTempPath(), "snipwhiz-tooldefaults-never-written"));

    [Fact]
    public void A_new_shape_inherits_every_property_of_the_remembered_style()
    {
        var settings = new Settings();
        var defaults = Defaults(settings);

        var edited = new RectangleAnnotation
        {
            Style = new AnnotationStyle
            {
                Stroke = Color.FromRgb(0x2F, 0xB3, 0x44),
                StrokeWidth = 3,
                Fill = Color.FromArgb(0x40, 0x3B, 0x82, 0xF6),
                Opacity = 0.8,
            },
        };
        defaults.Remember(edited);

        var fresh = (RectangleAnnotation)defaults.Apply(new RectangleAnnotation());

        // Asserted one property at a time on purpose. "The colour came through" is
        // exactly the observation that let a missing width go unnoticed.
        Assert.Equal(Color.FromRgb(0x2F, 0xB3, 0x44), fresh.Style.Stroke);
        Assert.Equal(3, fresh.Style.StrokeWidth);
        Assert.Equal(Color.FromArgb(0x40, 0x3B, 0x82, 0xF6), fresh.Style.Fill);
        Assert.Equal(0.8, fresh.Style.Opacity);
    }

    [Fact]
    public void A_style_remembered_for_one_tool_does_not_leak_into_another()
    {
        var settings = new Settings();
        var defaults = Defaults(settings);

        defaults.Remember(new RectangleAnnotation
        {
            Style = AnnotationStyle.Default with { StrokeWidth = 3 },
        });

        var ellipse = (EllipseAnnotation)defaults.Apply(new EllipseAnnotation());

        Assert.Equal(AnnotationStyle.Default.StrokeWidth, ellipse.Style.StrokeWidth);
    }

    [Fact]
    public void A_tool_with_nothing_remembered_keeps_what_its_own_type_constructs()
    {
        // The fallback is the type's constructor, not a shared default — which is
        // why a highlight comes up yellow and unstroked with no settings at all.
        var highlight = (HighlightAnnotation)Defaults(new Settings()).Apply(new HighlightAnnotation());

        Assert.Equal(HighlightAnnotation.DefaultStyle, highlight.Style);
    }

    [Fact]
    public void The_size_control_means_stroke_width_on_a_shape_and_font_size_on_text()
    {
        // One control, two meanings, and the object supplies the meaning. Text draws
        // no stroke at all, so before this the slider moved and nothing happened.
        var rectangle = new RectangleAnnotation { Style = AnnotationStyle.Default with { StrokeWidth = 3 } };
        Assert.Equal(3, rectangle.SizeControl);

        rectangle.SizeControl = 9;
        Assert.Equal(9, rectangle.Style.StrokeWidth);

        var text = new TextAnnotation { FontSize = 28 };
        Assert.Equal(28, text.SizeControl);

        text.SizeControl = 44;
        Assert.Equal(44, text.FontSize);
        // And it must not have quietly become a stroke, which would outline the plate.
        Assert.Equal(0, text.Style.StrokeWidth);
    }

    [Fact]
    public void A_remembered_font_size_reaches_the_next_caption()
    {
        // Font size is geometry, not style, so remembering the style alone left new
        // captions at the default while every shape kept its width.
        var settings = new Settings();
        var defaults = Defaults(settings);

        defaults.Remember(new TextAnnotation { Text = "sized", FontSize = 52 });

        Assert.Equal(52, ((TextAnnotation)defaults.Apply(new TextAnnotation())).FontSize);
    }

    [Fact]
    public void Remembering_survives_a_settings_round_trip()
    {
        var root = Path.Combine(Path.GetTempPath(), "snipwhiz-tooldefaults", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var settings = new Settings();
            var defaults = new ToolDefaults(settings, root);
            defaults.Remember(new ArrowAnnotation
            {
                Style = AnnotationStyle.Default with { StrokeWidth = 11, Stroke = Colors.Fuchsia },
            });
            defaults.Persist();

            // A fresh app start: reloaded from disk, not the object still in memory.
            var reloaded = new ToolDefaults(Settings.Load(root), root);
            var arrow = (ArrowAnnotation)reloaded.Apply(new ArrowAnnotation());

            Assert.Equal(11, arrow.Style.StrokeWidth);
            Assert.Equal(Colors.Fuchsia, arrow.Style.Stroke);
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
