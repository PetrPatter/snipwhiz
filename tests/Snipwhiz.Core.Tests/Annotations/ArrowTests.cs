using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Scene;
using Xunit;

namespace Snipwhiz.Core.Tests.Annotations;

/// <summary>
/// An arrow, checked against rendered pixels rather than against its own geometry.
///
/// <para>Both of the things that go wrong here are invisible to a geometry
/// assertion: the tip landing somewhere other than where the drag ended, and the
/// shaft running the full length so its flat cap lays a bar of stroke width across
/// the point. The second is why these tests use a <b>thick</b> arrow — at 2px the
/// bug is a rounding error, at 12px it is a blunt arrow.</para>
/// </summary>
public class ArrowTests
{
    private const int Size = 100;

    private static ArrowAnnotation Arrow(Point from, Point to, double strokeWidth)
    {
        var arrow = new ArrowAnnotation
        {
            Style = AnnotationStyle.Default with { Stroke = Colors.Black, StrokeWidth = strokeWidth },
        };
        arrow.Fit(from, to);
        return arrow;
    }

    /// <summary>A flat white canvas, so any ink at all is the arrow.</summary>
    private static BitmapSource Rendered(ArrowAnnotation arrow)
    {
        // Built inside the STA call: a BitmapSource belongs to the thread that made
        // it, and the render happens over there.
        return Sta.Run(() =>
        {
            var stride = Size * 4;
            var pixels = new byte[stride * Size];
            Array.Fill(pixels, (byte)255);
            var white = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);

            return Flattener.Render(
                white, new SceneDocument { CaptureId = Guid.Empty, Annotations = [arrow] });
        });
    }

    /// <summary>Anything meaningfully darker than the white canvas. Anti-aliasing lands well below this.</summary>
    private static bool[,] Ink(BitmapSource image)
    {
        var pixels = new byte[Size * Size * 4];
        image.CopyPixels(pixels, Size * 4, 0);

        var ink = new bool[Size, Size];
        for (var y = 0; y < Size; y++)
            for (var x = 0; x < Size; x++)
                ink[x, y] = pixels[(y * Size + x) * 4] < 200;   // blue channel; the arrow is black
        return ink;
    }

    // ---- the tip ----------------------------------------------------------

    /// <summary>
    /// The verification for this task: the point of the arrow is where the drag
    /// ended, not where the geometry happens to put it.
    /// </summary>
    [Fact]
    public void The_tip_lands_on_the_point_the_drag_ended_on()
    {
        var ink = Ink(Rendered(Arrow(new Point(10, 50), new Point(90, 50), strokeWidth: 6)));

        var furthest = -1;
        for (var x = 0; x < Size; x++)
            for (var y = 0; y < Size; y++)
                if (ink[x, y]) furthest = Math.Max(furthest, x);

        Assert.InRange(furthest, 89, 91);
    }

    /// <summary>
    /// <b>The negative control's target.</b> Two pixels back from the point, a real
    /// arrowhead is barely a pixel across. A shaft drawn the full length puts its
    /// flat cap there instead, so the column is the full stroke width.
    /// </summary>
    [Fact]
    public void A_thick_arrow_comes_to_a_point_rather_than_a_blunt_end()
    {
        const double strokeWidth = 12;
        var ink = Ink(Rendered(Arrow(new Point(10, 50), new Point(90, 50), strokeWidth)));

        var column = 0;
        for (var y = 0; y < Size; y++) if (ink[88, y]) column++;

        Assert.True(column < strokeWidth / 2,
            $"{column} inked pixels two back from the tip: the arrow is blunt, not pointed.");
    }

    [Fact]
    public void The_shaft_is_still_drawn_behind_the_head()
    {
        // The other way to pass the pointedness test is to draw no shaft at all.
        var ink = Ink(Rendered(Arrow(new Point(10, 50), new Point(90, 50), strokeWidth: 6)));

        Assert.True(ink[20, 50]);
        Assert.True(ink[40, 50]);
    }

    [Fact]
    public void A_diagonal_arrows_tip_lands_on_its_end_point()
    {
        // Axis-aligned hides a swapped normal; diagonal does not.
        var ink = Ink(Rendered(Arrow(new Point(15, 15), new Point(80, 80), strokeWidth: 6)));

        var near = false;
        for (var dx = -1; dx <= 1; dx++)
            for (var dy = -1; dy <= 1; dy++)
                near |= ink[80 + dx, 80 + dy];

        Assert.True(near, "no ink within a pixel of the diagonal arrow's tip.");
        Assert.False(ink[85, 85], "ink past the tip.");
    }

    // ---- the head is clickable --------------------------------------------

    [Fact]
    public void The_head_is_hit_where_it_is_wider_than_the_shaft()
    {
        // Halfway along a 48px head the arrow is ~24px across; the shaft is 12.
        var arrow = Arrow(new Point(10, 50), new Point(90, 50), strokeWidth: 12);

        Assert.True(arrow.HitTest(new Point(66, 60), 0), "the flank of the head is not clickable.");
        Assert.False(arrow.HitTest(new Point(40, 60), 0), "beside the shaft should not be a hit.");
    }

    [Fact]
    public void Nothing_past_the_tip_is_a_hit()
    {
        var arrow = Arrow(new Point(10, 50), new Point(90, 50), strokeWidth: 12);

        Assert.True(arrow.HitTest(new Point(89, 50), 0));
        Assert.False(arrow.HitTest(new Point(120, 50), 0));
    }

    // ---- it is still a line ------------------------------------------------

    [Fact]
    public void Resizing_an_arrow_keeps_the_end_it_points_at()
    {
        // Inherited from LineAnnotation, asserted here because a reversed line looks
        // like a line and a reversed arrow points at the wrong thing.
        var upLeft = Arrow(new Point(300, 300), new Point(100, 100), strokeWidth: 4);

        var resized = (LineGeometryState)upLeft.GeometryForBounds(new Size(400, 400));

        Assert.True(resized.Delta.X < 0 && resized.Delta.Y < 0);
    }

    [Fact]
    public void A_short_arrow_keeps_a_shaft_instead_of_being_all_head()
    {
        // A head sized purely from the stroke width would be longer than this arrow.
        var ink = Ink(Rendered(Arrow(new Point(40, 50), new Point(60, 50), strokeWidth: 10)));

        Assert.True(ink[42, 50], "the whole arrow became a head.");
    }
}
