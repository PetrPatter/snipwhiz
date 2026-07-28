using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Scene;
using Xunit;

namespace Snipwhiz.Core.Tests.Imaging;

public class FlattenerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snipwhiz-tests", Guid.NewGuid().ToString("N"));

    public FlattenerTests() => Directory.CreateDirectory(_dir);

    private static readonly Guid CaptureId = Guid.Parse("0198f2c1-4a5b-7c6d-8e9f-a0b1c2d3e4f5");

    /// <summary>A flat white capture, so anything drawn on it is unmistakable.</summary>
    private static BitmapSource White(int width = 100, int height = 100)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)255);
        return BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
    }

    private static SceneDocument Scene(params Annotation[] annotations) =>
        new() { CaptureId = CaptureId, Annotations = [.. annotations] };

    private static RectangleAnnotation Filled(Point from, Point to, Color colour, int z = 0) =>
        new()
        {
            ZIndex = z,
            Size = new Rect(from, to).Size,
            Transform = new Matrix(1, 0, 0, 1,
                (from.X + to.X) / 2, (from.Y + to.Y) / 2),
            Style = new AnnotationStyle { Fill = colour, StrokeWidth = 0 },
        };

    private static Color PixelAt(BitmapSource image, int x, int y)
    {
        var pixel = new byte[4];
        image.CopyPixels(new Int32Rect(x, y, 1, 1), pixel, 4, 0);
        return Color.FromArgb(pixel[3], pixel[2], pixel[1], pixel[0]);
    }

    // ---- rendering --------------------------------------------------------

    [Fact]
    public void An_annotation_is_drawn_where_it_was_placed_and_nowhere_else()
    {
        var rendered = Sta.Run(() =>
            Flattener.Render(White(), Scene(Filled(new Point(10, 10), new Point(30, 30), Colors.Red))));

        Assert.Equal(Colors.Red, PixelAt(rendered, 20, 20));
        Assert.Equal(Colors.White, PixelAt(rendered, 50, 50));
    }

    [Fact]
    public void An_empty_scene_leaves_the_capture_untouched()
    {
        var rendered = Sta.Run(() => Flattener.Render(White(), Scene()));

        Assert.Equal(100, rendered.PixelWidth);
        Assert.Equal(100, rendered.PixelHeight);
        Assert.Equal(Colors.White, PixelAt(rendered, 0, 0));
        Assert.Equal(Colors.White, PixelAt(rendered, 50, 50));
        Assert.Equal(Colors.White, PixelAt(rendered, 99, 99));
    }

    [Fact]
    public void Later_objects_paint_over_earlier_ones()
    {
        var rendered = Sta.Run(() => Flattener.Render(White(), Scene(
            Filled(new Point(10, 10), new Point(50, 50), Colors.Red, z: 0),
            Filled(new Point(30, 30), new Point(70, 70), Colors.Blue, z: 1))));

        Assert.Equal(Colors.Red, PixelAt(rendered, 20, 20));     // only the first
        Assert.Equal(Colors.Blue, PixelAt(rendered, 40, 40));    // overlap, later wins
        Assert.Equal(Colors.Blue, PixelAt(rendered, 60, 60));    // only the second
    }

    [Fact]
    public void Paint_order_follows_z_not_list_order()
    {
        // The blue one is listed first but sits on top. Rendering in list order
        // instead of z order puts red on top and this fails.
        var rendered = Sta.Run(() => Flattener.Render(White(), Scene(
            Filled(new Point(10, 10), new Point(50, 50), Colors.Blue, z: 5),
            Filled(new Point(10, 10), new Point(50, 50), Colors.Red, z: 1))));

        Assert.Equal(Colors.Blue, PixelAt(rendered, 30, 30));
    }

    [Fact]
    public void A_rotated_annotation_lands_where_the_hit_test_says_it_is()
    {
        var transform = Matrix.Identity;
        transform.Rotate(45);
        transform.Translate(50, 50);
        var square = new RectangleAnnotation
        {
            Size = new Size(40, 40),
            Transform = transform,
            Style = new AnnotationStyle { Fill = Colors.Red, StrokeWidth = 0 },
        };

        var rendered = Sta.Run(() => Flattener.Render(White(), Scene(square)));

        // Centre is inside the diamond; the corner of its bounding box is not.
        Assert.True(square.HitTest(new Point(50, 50), 0));
        Assert.Equal(Colors.Red, PixelAt(rendered, 50, 50));

        var corner = new Point(50 + 26, 50 + 26);
        Assert.False(square.HitTest(corner, 0));
        Assert.Equal(Colors.White, PixelAt(rendered, (int)corner.X, (int)corner.Y));
    }

    // ---- crop -------------------------------------------------------------

    [Fact]
    public void A_crop_changes_the_output_size_and_moves_the_content_with_it()
    {
        var scene = Scene(Filled(new Point(40, 40), new Point(60, 60), Colors.Red));
        scene.Crop = new Rect(30, 30, 40, 40);

        var rendered = Sta.Run(() => Flattener.Render(White(), scene));

        Assert.Equal(40, rendered.PixelWidth);
        Assert.Equal(40, rendered.PixelHeight);
        // The rectangle was at image (50,50); the crop starts at (30,30), so it
        // is now at (20,20). Forgetting the offset leaves it at (50,50) — outside.
        Assert.Equal(Colors.Red, PixelAt(rendered, 20, 20));
        Assert.Equal(Colors.White, PixelAt(rendered, 2, 2));
    }

    [Fact]
    public void An_annotation_outside_the_crop_does_not_appear()
    {
        var scene = Scene(Filled(new Point(5, 5), new Point(15, 15), Colors.Red));
        scene.Crop = new Rect(50, 50, 40, 40);

        var rendered = Sta.Run(() => Flattener.Render(White(), scene));

        // (10,10) is where the rectangle would land if the crop offset were
        // dropped — its image coordinates read as output coordinates. Sampling
        // anywhere else lets that bug through, which an earlier version of this
        // test did.
        Assert.Equal(Colors.White, PixelAt(rendered, 10, 10));
        Assert.Equal(Colors.White, PixelAt(rendered, 0, 0));
        Assert.Equal(Colors.White, PixelAt(rendered, 20, 20));
    }

    [Fact]
    public void A_crop_reaching_outside_the_capture_is_clamped_rather_than_padded()
    {
        // Reachable from a hand-edited project. Rendering it as written would put
        // transparent margins in an image that has none.
        var scene = Scene();
        scene.Crop = new Rect(80, 80, 200, 200);

        var rendered = Sta.Run(() => Flattener.Render(White(), scene));

        Assert.Equal(20, rendered.PixelWidth);
        Assert.Equal(20, rendered.PixelHeight);
        Assert.Equal(Colors.White, PixelAt(rendered, 19, 19));
    }

    [Fact]
    public void A_degenerate_crop_falls_back_to_the_whole_capture()
    {
        var scene = Scene();
        scene.Crop = new Rect(10, 10, 0, 0);

        var rendered = Sta.Run(() => Flattener.Render(White(), scene));

        Assert.Equal(100, rendered.PixelWidth);
    }

    // ---- save -------------------------------------------------------------

    [Fact]
    public void Save_writes_a_png_that_reloads_with_the_same_pixels()
    {
        var path = Path.Combine(_dir, "flat.png");

        var reloaded = Sta.Run(() =>
        {
            Flattener.Save(path, White(), Scene(Filled(new Point(10, 10), new Point(30, 30), Colors.Red)));

            using var stream = File.OpenRead(path);
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            return (BitmapSource)frame;
        });

        Assert.Equal(100, reloaded.PixelWidth);
        Assert.Equal(Colors.Red, PixelAt(reloaded, 20, 20));
        Assert.Equal(Colors.White, PixelAt(reloaded, 50, 50));
    }

    [Fact]
    public void Save_leaves_no_temporary_file_behind()
    {
        var path = Path.Combine(_dir, "clean.png");
        Sta.Run(() => Flattener.Save(path, White(), Scene()));

        Assert.True(File.Exists(path));
        Assert.Empty(Directory.GetFiles(_dir, "*.tmp"));
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
