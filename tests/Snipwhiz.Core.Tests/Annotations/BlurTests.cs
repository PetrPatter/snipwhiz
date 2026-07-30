using System.Diagnostics;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Scene;
using Xunit;
using Xunit.Abstractions;

namespace Snipwhiz.Core.Tests.Annotations;

/// <summary>
/// A blur, measured rather than looked at.
///
/// <para>"It looks blurry" is not a claim about a redaction tool. What matters is
/// that the detail is <b>gone</b> — so these measure how much variation survives
/// inside the region, against an unblurred control that shows what the measurement
/// looks like when it fails.</para>
///
/// <para>The worst failure available here is not a soft blur; it is a
/// <b>correct-looking blur of the wrong pixels</b>. Revision 1 of §4.9 said
/// recompute on resize but not on move, which drags a stale patch across the
/// picture and displays the content the tool was placed there to hide, entirely
/// convincingly. <see cref="Moving_a_blur_reblurs_where_it_landed"/> is the test
/// that exists for that, and it is the one to keep working.</para>
/// </summary>
public class BlurTests(ITestOutputHelper output)
{
    private const int Size = 64;

    /// <summary>Hard 2px stripes: maximum local contrast, so surviving detail is obvious.</summary>
    private static BitmapSource Stripes()
    {
        var stride = Size * 4;
        var pixels = new byte[stride * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var i = y * stride + x * 4;
                var on = (x / 2 + y / 2) % 2 == 0;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)(on ? 255 : 0);
                pixels[i + 3] = 255;
            }
        }
        var source = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    /// <summary>A capture with one bright patch, for proving which pixels were sampled.</summary>
    private static BitmapSource Marked()
    {
        var stride = Size * 4;
        var pixels = new byte[stride * Size];
        for (var i = 0; i < pixels.Length; i += 4)
        {
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 0;
            pixels[i + 3] = 255;
        }
        // A white square in the top-left quarter only.
        for (var y = 0; y < 24; y++)
        {
            for (var x = 0; x < 24; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
            }
        }
        var source = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private static BlurAnnotation Blur(Rect area, double radius)
    {
        var blur = new BlurAnnotation { Radius = radius };
        blur.Fit(area.TopLeft, area.BottomRight);
        return blur;
    }

    private static byte[] Render(BitmapSource source, params Annotation[] annotations) => Sta.Run(() =>
    {
        var rendered = Flattener.Render(
            source, new SceneDocument { CaptureId = Guid.Empty, Annotations = [.. annotations] });

        var pixels = new byte[Size * Size * 4];
        rendered.CopyPixels(pixels, Size * 4, 0);
        return pixels;
    });

    private static byte Grey(byte[] pixels, int x, int y) => pixels[(y * Size + x) * 4];

    /// <summary>
    /// Mean absolute difference between neighbouring pixels inside a region. High on
    /// hard stripes, near zero on anything genuinely smoothed — and unlike a variance
    /// over the whole region, it does not reward a blur that merely shifted the
    /// detail somewhere else.
    /// </summary>
    private static double LocalContrast(byte[] pixels, Rect area)
    {
        double total = 0;
        var count = 0;
        for (var y = (int)area.Y; y < area.Bottom - 1; y++)
        {
            for (var x = (int)area.X; x < area.Right - 1; x++)
            {
                total += Math.Abs(Grey(pixels, x, y) - Grey(pixels, x + 1, y));
                total += Math.Abs(Grey(pixels, x, y) - Grey(pixels, x, y + 1));
                count += 2;
            }
        }
        return count == 0 ? 0 : total / count;
    }

    [Fact]
    public void Detail_inside_a_blur_does_not_survive_it()
    {
        var area = new Rect(8, 8, 48, 48);
        var source = Stripes();

        var before = LocalContrast(Render(source), area);
        var after = LocalContrast(Render(source, Blur(area, radius: 8)), area);

        output.WriteLine($"local contrast {before:F1} -> {after:F1}");

        // The control is the first number: hard stripes sit near 127, so a threshold
        // of 2 is not a value chosen to pass — it is two orders of magnitude down.
        Assert.True(before > 100, $"the source was not detailed to begin with ({before:F1})");
        Assert.True(after < 2, $"detail survived the blur ({after:F1})");
    }

    [Fact]
    public void Outside_the_region_nothing_is_touched()
    {
        var source = Stripes();
        var before = Render(source);
        var after = Render(source, Blur(new Rect(8, 8, 32, 32), radius: 8));

        Assert.Equal(Grey(before, 55, 55), Grey(after, 55, 55));
    }

    /// <summary>
    /// The one that matters. A blur whose cache is keyed without its position keeps
    /// showing the pixels it was computed over, so moving it off a secret leaves the
    /// secret's blur behind and — far worse — moving it <i>onto</i> one shows the
    /// secret through a blur of somewhere else.
    /// </summary>
    [Fact]
    public void Moving_a_blur_reblurs_where_it_landed()
    {
        var source = Marked();
        var blur = Blur(new Rect(0, 0, 24, 24), radius: 6);

        // Over the white patch: bright.
        var overWhite = Render(source, blur);
        var bright = Grey(overWhite, 12, 12);

        // The same object moved onto black. Rendering it again must not reuse what
        // it computed over the white patch.
        var moved = blur.Transform;
        moved.OffsetY += 36;
        blur.Transform = moved;

        var overBlack = Render(source, blur);
        var dark = Grey(overBlack, 12, 48);

        output.WriteLine($"over white {bright}, after moving onto black {dark}");

        Assert.True(bright > 200, $"the blur did not start over the white patch ({bright})");
        Assert.True(dark < 40, $"a stale patch moved with the blur ({dark})");
    }

    [Fact]
    public void Changing_the_radius_recomputes()
    {
        var area = new Rect(8, 8, 48, 48);
        var source = Stripes();
        var blur = Blur(area, radius: 2);

        var tight = LocalContrast(Render(source, blur), area);
        blur.Radius = 20;
        var wide = LocalContrast(Render(source, blur), area);

        Assert.NotEqual(tight, wide);
    }

    [Fact]
    public void A_second_render_of_an_unchanged_blur_is_identical()
    {
        var source = Stripes();
        var blur = Blur(new Rect(8, 8, 48, 48), radius: 8);

        Assert.Equal(Render(source, blur), Render(source, blur));
    }

    [Fact]
    public void A_shape_hidden_under_a_blur_does_not_reach_it()
    {
        var source = Stripes();
        var area = new Rect(8, 8, 32, 32);

        var hidden = new RectangleAnnotation
        {
            ZIndex = 0,
            Style = new AnnotationStyle { Stroke = Colors.Red, StrokeWidth = 0, Fill = Colors.Red },
        };
        hidden.Fit(area.TopLeft, area.BottomRight);

        var alone = Render(source, Blur(area, radius: 8));
        var over = Render(source, hidden, Blur(area, radius: 8));

        Assert.Equal(Grey(alone, 20, 20), Grey(over, 20, 20));
    }

    [Fact]
    public void A_blur_hanging_off_the_edge_of_the_capture_still_renders()
    {
        var pixels = Render(Stripes(), Blur(new Rect(-20, -20, 40, 40), radius: 8));

        Assert.True(LocalContrast(pixels, new Rect(2, 2, 16, 16)) < 2);
    }

    [Fact]
    public void The_size_control_is_the_radius()
    {
        var blur = new BlurAnnotation();
        var stroke = blur.Style.StrokeWidth;

        blur.SizeControl = 15;

        Assert.Equal(15, blur.Radius);
        Assert.Equal(stroke, blur.Style.StrokeWidth);
        Assert.Equal((2, 40), blur.SizeControlRange);
    }

    [Fact]
    public void Resizing_keeps_the_radius()
    {
        var blur = Blur(new Rect(0, 0, 32, 32), radius: 17);

        blur.RestoreGeometry(blur.GeometryForBounds(new Size(64, 64)));

        Assert.Equal(17, blur.Radius);
        Assert.Equal(new Size(64, 64), blur.Size);
    }

    /// <summary>
    /// The measurement behind dropping the plan's background-thread Gaussian, and
    /// the thing that has to keep being true for that to stay the right call.
    ///
    /// <para>The worst case a user can ask for — the whole of a 4K capture at the
    /// widest radius — takes about <b>40 ms</b>. It took 2,233 ms when this test was
    /// written, which is what a full-resolution blur costs: the vertical pass strides
    /// a row at a time and misses cache on essentially every read. Blurring a shrunk
    /// copy removed that, and a background thread with it.</para>
    ///
    /// <para>The threshold is an order of magnitude above the measurement, so it
    /// tolerates a slow machine while still failing loudly if the shrink is ever
    /// removed. It is not policing milliseconds; it is guarding a design decision.</para>
    /// </summary>
    [Fact]
    public void A_full_screen_blur_at_maximum_radius_is_not_slow()
    {
        var elapsed = Sta.Run(() =>
        {
            const int width = 3840;
            const int height = 2160;
            var stride = width * 4;
            var pixels = new byte[stride * height];
            Array.Fill(pixels, (byte)128);
            var source = BitmapSource.Create(
                width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
            source.Freeze();

            var blur = Blur(new Rect(0, 0, width, height), radius: 40);
            var visual = new DrawingVisual();

            var watch = Stopwatch.StartNew();
            using (var dc = visual.RenderOpen()) blur.Render(dc, source);
            return watch.ElapsedMilliseconds;
        });

        output.WriteLine($"3840x2160 at radius 40: {elapsed} ms");
        Assert.True(elapsed < 400, $"a full-screen blur took {elapsed} ms");
    }
}
