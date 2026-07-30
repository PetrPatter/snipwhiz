using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Scene;
using Xunit;

namespace Snipwhiz.Core.Tests.Annotations;

/// <summary>
/// The first annotation whose appearance comes from pixels it does not own.
///
/// <para>Two things can go wrong here and neither is visible to a geometry
/// assertion. The blocks can come back <b>soft</b> — a downscale followed by a
/// filtered upscale is a blur, which is a different tool drawn convincingly and
/// hides nothing a determined reader cannot recover. And it can sample the
/// <b>composite</b> rather than the capture, which §4.9 rules out: that would make
/// paint order load-bearing and cycle when two of them overlap.</para>
/// </summary>
public class PixelateTests
{
    private const int Size = 64;

    /// <summary>
    /// A left-to-right ramp with a hard checker laid over it.
    ///
    /// <para>The checker is the detail a pixelate has to destroy. The <b>ramp</b> is
    /// what makes neighbouring blocks average to different greys — the first draft
    /// of this file used the checker alone, and every 8px block averaged to exactly
    /// mid-grey, so the suite could not tell a real pixelate from one flat rectangle
    /// painted over the whole region.</para>
    /// </summary>
    private static BitmapSource Stripes()
    {
        var stride = Size * 4;
        var pixels = new byte[stride * Size];
        for (var y = 0; y < Size; y++)
        {
            for (var x = 0; x < Size; x++)
            {
                var i = y * stride + x * 4;
                var value = 30 + x * 3 + ((x / 2 + y / 2) % 2 == 0 ? 25 : -25);
                pixels[i] = pixels[i + 1] = pixels[i + 2] = (byte)value;
                pixels[i + 3] = 255;
            }
        }
        var source = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private static PixelateAnnotation Pixelate(Rect area, double block)
    {
        var p = new PixelateAnnotation { BlockSize = block };
        p.Fit(area.TopLeft, area.BottomRight);
        return p;
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

    [Fact]
    public void Each_block_comes_back_flat()
    {
        var pixels = Render(Stripes(), Pixelate(new Rect(0, 0, 32, 32), block: 8));

        // Every pixel of the top-left block must match its corner. A filtered
        // upscale — the default, and the whole reason NearestNeighbor is set on the
        // brush — leaves a gradient here instead.
        var corner = Grey(pixels, 0, 0);
        for (var y = 0; y < 8; y++)
        {
            for (var x = 0; x < 8; x++) Assert.Equal(corner, Grey(pixels, x, y));
        }
    }

    [Fact]
    public void Neighbouring_blocks_do_not_all_come_back_the_same()
    {
        var pixels = Render(Stripes(), Pixelate(new Rect(0, 0, 32, 32), block: 8));

        // Or "every block is flat" would also be true of a tool that painted one
        // flat rectangle over everything, which is not what this is.
        var distinct = new HashSet<byte>();
        for (var by = 0; by < 4; by++)
        {
            for (var bx = 0; bx < 4; bx++) distinct.Add(Grey(pixels, bx * 8 + 3, by * 8 + 3));
        }
        Assert.True(distinct.Count > 1, "every block came back the same colour");
    }

    [Fact]
    public void Outside_the_region_nothing_is_touched()
    {
        var source = Stripes();
        var before = Render(source);
        var after = Render(source, Pixelate(new Rect(0, 0, 32, 32), block: 8));

        Assert.Equal(Grey(before, 40, 40), Grey(after, 40, 40));
        Assert.NotEqual(Grey(before, 3, 3), Grey(after, 3, 3));
    }

    /// <summary>
    /// §4.9's documented consequence, asserted rather than trusted: what is under a
    /// pixelate does not reach its blocks.
    ///
    /// <para>The second half is what stops this being vacuous. Pixelating a capture
    /// that <i>already contains</i> the shape gives a visibly different result, so
    /// "identical" above is a real property of sampling the original and not an
    /// artefact of the shape being invisible either way.</para>
    /// </summary>
    [Fact]
    public void A_shape_hidden_under_a_pixelate_does_not_reach_its_blocks()
    {
        var source = Stripes();
        var area = new Rect(0, 0, 32, 32);

        var hidden = new RectangleAnnotation
        {
            ZIndex = 0,
            Style = new AnnotationStyle { Stroke = Colors.White, StrokeWidth = 0, Fill = Colors.White },
        };
        hidden.Fit(area.TopLeft, area.BottomRight);

        var alone = Render(source, Pixelate(area, block: 8));
        var over = Render(source, hidden, Pixelate(area, block: 8));

        for (var i = 0; i < 4; i++)
        {
            Assert.Equal(Grey(alone, i * 8 + 3, 3), Grey(over, i * 8 + 3, 3));
        }

        // The control: burn the same shape into the capture and the blocks move.
        var composite = Sta.Run(() => Flattener.Render(
            source, new SceneDocument { CaptureId = Guid.Empty, Annotations = [hidden] }));
        var sampledFromComposite = Render(composite, Pixelate(area, block: 8));

        Assert.NotEqual(Grey(alone, 3, 3), Grey(sampledFromComposite, 3, 3));
    }

    [Fact]
    public void A_block_larger_than_the_region_flattens_it_rather_than_throwing()
    {
        var pixels = Render(Stripes(), Pixelate(new Rect(0, 0, 10, 10), block: 40));

        Assert.Equal(Grey(pixels, 1, 1), Grey(pixels, 8, 8));
    }

    [Fact]
    public void A_pixelate_hanging_off_the_edge_of_the_capture_still_renders()
    {
        // CroppedBitmap throws on a rect that leaves the source, and dragging a
        // redaction off the edge of the picture is an ordinary thing to do.
        var pixels = Render(Stripes(), Pixelate(new Rect(-20, -20, 40, 40), block: 8));

        Assert.NotEqual(Grey(Render(Stripes()), 3, 3), Grey(pixels, 3, 3));
    }

    [Fact]
    public void The_size_control_is_the_block_size()
    {
        var pixelate = new PixelateAnnotation();
        var stroke = pixelate.Style.StrokeWidth;

        pixelate.SizeControl = 20;

        Assert.Equal(20, pixelate.BlockSize);
        Assert.Equal(stroke, pixelate.Style.StrokeWidth);
        Assert.Equal((2, 40), pixelate.SizeControlRange);
    }

    [Fact]
    public void Block_size_is_captured_and_restored_as_geometry()
    {
        var pixelate = Pixelate(new Rect(0, 0, 32, 32), block: 8);
        var before = pixelate.CaptureGeometry();

        pixelate.BlockSize = 30;
        pixelate.RestoreGeometry(before);

        Assert.Equal(8, pixelate.BlockSize);
    }

    /// <summary>
    /// Resizing must carry the block size through. <c>GeometryForBounds</c> returning
    /// a state without it would silently reset every redaction to the default the
    /// first time it was dragged.
    /// </summary>
    [Fact]
    public void Resizing_keeps_the_block_size()
    {
        var pixelate = Pixelate(new Rect(0, 0, 32, 32), block: 25);

        pixelate.RestoreGeometry(pixelate.GeometryForBounds(new Size(64, 64)));

        Assert.Equal(25, pixelate.BlockSize);
        Assert.Equal(new Size(64, 64), pixelate.Size);
    }
}
