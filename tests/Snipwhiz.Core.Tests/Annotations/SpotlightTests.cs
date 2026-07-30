using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Scene;
using Xunit;

namespace Snipwhiz.Core.Tests.Annotations;

/// <summary>
/// The only tool here whose subject is the part it does not paint.
///
/// <para>Which is what makes it worth testing in pixels: every assertion about a
/// spotlight is about somewhere <b>else</b>, and the two ways to get it wrong —
/// filling the lit region instead of the surround, and dimming nothing at all —
/// both leave an object that is present, selectable and completely wrong.</para>
/// </summary>
public class SpotlightTests
{
    private const int Size = 64;

    private static BitmapSource White()
    {
        var stride = Size * 4;
        var pixels = new byte[stride * Size];
        Array.Fill(pixels, (byte)255);
        var source = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private static SpotlightAnnotation Spotlight(Rect area)
    {
        var spotlight = new SpotlightAnnotation();
        spotlight.Fit(area.TopLeft, area.BottomRight);
        return spotlight;
    }

    private static byte[] Render(params Annotation[] annotations) => Sta.Run(() =>
    {
        var rendered = Flattener.Render(
            White(), new SceneDocument { CaptureId = Guid.Empty, Annotations = [.. annotations] });

        var pixels = new byte[Size * Size * 4];
        rendered.CopyPixels(pixels, Size * 4, 0);
        return pixels;
    });

    private static byte Grey(byte[] pixels, int x, int y) => pixels[(y * Size + x) * 4];

    [Fact]
    public void The_lit_region_is_untouched_and_everything_else_is_darker()
    {
        var pixels = Render(Spotlight(new Rect(20, 20, 24, 24)));

        Assert.Equal(255, Grey(pixels, 32, 32));                 // inside, on a white capture
        Assert.True(Grey(pixels, 4, 4) < 200, "the surround was not dimmed");
        Assert.True(Grey(pixels, 60, 60) < 200, "the far corner was not dimmed");
    }

    /// <summary>
    /// The dim has to reach the edges of the picture, which is the one thing a
    /// spotlight needs the capture for — it samples no pixels, only its size.
    /// </summary>
    [Fact]
    public void The_dim_reaches_every_edge_of_the_capture()
    {
        var pixels = Render(Spotlight(new Rect(28, 28, 8, 8)));

        foreach (var (x, y) in new[] { (0, 0), (Size - 1, 0), (0, Size - 1), (Size - 1, Size - 1) })
        {
            Assert.True(Grey(pixels, x, y) < 200, $"({x},{y}) was not dimmed");
        }
    }

    [Fact]
    public void The_size_control_is_the_dim_strength()
    {
        var spotlight = new SpotlightAnnotation();

        spotlight.SizeControl = 80;

        Assert.Equal(0.8, spotlight.Style.Opacity, precision: 6);
        Assert.Equal(80, spotlight.SizeControl);
        Assert.Equal((10, 95), spotlight.SizeControlRange);
    }

    [Fact]
    public void A_stronger_dim_is_darker()
    {
        var weak = Spotlight(new Rect(20, 20, 24, 24));
        weak.SizeControl = 20;
        var strong = Spotlight(new Rect(20, 20, 24, 24));
        strong.SizeControl = 90;

        Assert.True(Grey(Render(strong), 4, 4) < Grey(Render(weak), 4, 4));
    }

    /// <summary>
    /// Clicking the lit region must reach whatever is being spotlit. A spotlight
    /// that hit-tests its own hole is one that cannot be seen past.
    /// </summary>
    [Fact]
    public void Clicking_inside_the_light_does_not_select_the_spotlight()
    {
        var spotlight = Spotlight(new Rect(20, 20, 24, 24));

        Assert.False(spotlight.HitTest(new Point(32, 32), tolerance: 2));
    }

    [Fact]
    public void The_edge_of_the_light_can_be_grabbed()
    {
        var spotlight = Spotlight(new Rect(20, 20, 24, 24));

        Assert.True(spotlight.HitTest(new Point(20, 32), tolerance: 2));
    }

    /// <summary>
    /// And it must not swallow clicks across the rest of the picture, even though
    /// the rest of the picture is what it paints.
    /// </summary>
    [Fact]
    public void Clicking_far_outside_does_not_select_the_spotlight()
    {
        var spotlight = Spotlight(new Rect(20, 20, 24, 24));

        Assert.False(spotlight.HitTest(new Point(2, 2), tolerance: 2));
    }
}
