using System.Windows;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;
using Xunit;

namespace Snipwhiz.Core.Tests.Annotations;

/// <summary>
/// Rotated hit-testing, proven against hand-computed points while it is still pure
/// functions — spec 2b risk 4, and the reason this is task 4 rather than part of
/// the selection UI.
///
/// <para>A 100×100 square centred on the origin and turned 45° has a bounding box
/// of 141.4×141.4. The corners of that box are a long way outside the shape, which
/// is what makes the naïve implementation — test <c>Bounds</c> — pass the easy case
/// and fail the real one.</para>
/// </summary>
public class HitTestTests
{
    private static RectangleAnnotation Square(double degrees, double size = 100)
    {
        var transform = Matrix.Identity;
        transform.Rotate(degrees);
        return new RectangleAnnotation
        {
            Size = new Size(size, size),
            Transform = transform,
            Style = AnnotationStyle.Default with { StrokeWidth = 0 },
        };
    }

    [Fact]
    public void An_unrotated_shape_is_hit_inside_and_missed_outside()
    {
        var square = Square(0);

        Assert.True(square.HitTest(new Point(0, 0), 0));
        Assert.True(square.HitTest(new Point(49, 49), 0));
        Assert.False(square.HitTest(new Point(51, 0), 0));
    }

    [Fact]
    public void A_point_inside_a_rotated_shape_is_hit()
    {
        // (40,0) pulls back through -45° to (28.3, -28.3), inside [-50,50].
        Assert.True(Square(45).HitTest(new Point(40, 0), 0));
    }

    /// <summary>
    /// THE test, and it discriminates against both plausible wrong implementations
    /// with one probe point.
    ///
    /// <para>A 200×60 rectangle turned 30°. The probe is chosen as
    /// <c>R(-30°)·(95, 0)</c>, so:</para>
    /// <list type="bullet">
    /// <item>correct — pulling it back through the inverse gives (47.5, −82.3),
    /// whose y is far outside the 60-tall shape, so it misses;</item>
    /// <item><b>applying the transform instead of its inverse</b> gives (95, 0),
    /// comfortably inside, so it hits;</item>
    /// <item><b>testing Bounds</b> hits, since the probe is well within the
    /// 203×152 bounding box.</item>
    /// </list>
    ///
    /// <para>Both wrong versions were run against this file. An earlier form of
    /// this test used a square at 45°, where rotating the probe the wrong way lands
    /// it symmetrically outside — so it caught the Bounds bug and missed the
    /// inverse bug entirely. Symmetry is what makes a hit-test suite look like it
    /// discriminates when it does not.</para>
    /// </summary>
    [Fact]
    public void A_point_inside_the_bounding_box_but_outside_the_rotated_shape_is_missed()
    {
        var transform = Matrix.Identity;
        transform.Rotate(30);
        var shape = new RectangleAnnotation
        {
            Size = new Size(200, 60),
            Transform = transform,
            Style = AnnotationStyle.Default with { StrokeWidth = 0 },
        };

        var probe = new Point(82.2724, -47.5);

        Assert.True(shape.Bounds.Contains(probe));
        Assert.False(shape.HitTest(probe, 0));
    }

    [Fact]
    public void Rotation_turns_a_shape_about_its_own_centre()
    {
        // Geometry is centred on the origin precisely so this needs no correction
        // term. A square is its own bounding box at 0° and grows by root two at 45°,
        // staying centred either way.
        var upright = Square(0).Bounds;
        var turned = Square(45).Bounds;

        Assert.Equal(0, upright.X + upright.Width / 2, 6);
        Assert.Equal(0, turned.X + turned.Width / 2, 6);
        Assert.Equal(100, upright.Width, 6);
        Assert.Equal(141.42, turned.Width, 2);
    }

    [Fact]
    public void A_translated_and_rotated_shape_is_hit_relative_to_where_it_sits()
    {
        var transform = Matrix.Identity;
        transform.Rotate(30);
        transform.Translate(500, 300);
        var shape = new RectangleAnnotation
        {
            Size = new Size(200, 40),
            Transform = transform,
            Style = AnnotationStyle.Default with { StrokeWidth = 0 },
        };

        Assert.True(shape.HitTest(new Point(500, 300), 0));      // its centre
        Assert.False(shape.HitTest(new Point(0, 0), 0));         // the origin it was moved from
        // Along the shape's own long axis, 30° up-and-right from the centre.
        Assert.True(shape.HitTest(new Point(500 + 90 * Math.Cos(Math.PI / 6),
                                            300 + 90 * Math.Sin(Math.PI / 6)), 0));
    }

    [Fact]
    public void Tolerance_widens_the_shape_by_the_same_amount_at_any_angle()
    {
        // Because the transform is a rigid motion, a tolerance in image space means
        // the same distance in local space. If scale ever enters the transform this
        // is the assertion that breaks.
        Assert.False(Square(0).HitTest(new Point(54, 0), 0));
        Assert.True(Square(0).HitTest(new Point(54, 0), 5));

        var diagonal = 50 * Math.Sqrt(2);
        Assert.False(Square(45).HitTest(new Point(diagonal + 4, 0), 0));
        Assert.True(Square(45).HitTest(new Point(diagonal + 4, 0), 5));
    }

    [Fact]
    public void A_thick_stroke_is_clickable_where_it_is_drawn()
    {
        // Half the stroke sits outside the geometry, so the visible edge of a
        // 20px outline extends 10px past the rect.
        var thick = new RectangleAnnotation
        {
            Size = new Size(100, 100),
            Style = AnnotationStyle.Default with { StrokeWidth = 20 },
        };

        Assert.True(thick.HitTest(new Point(58, 0), 0));
        Assert.False(thick.HitTest(new Point(62, 0), 0));
    }

    [Fact]
    public void FromDrag_spans_the_two_points_whichever_way_it_was_dragged()
    {
        var forward = RectangleAnnotation.FromDrag(new Point(100, 50), new Point(300, 150));
        var backward = RectangleAnnotation.FromDrag(new Point(300, 150), new Point(100, 50));

        Assert.Equal(new Size(200, 100), forward.Size);
        Assert.Equal(forward.Size, backward.Size);
        Assert.Equal(forward.Bounds, backward.Bounds);
        Assert.True(forward.HitTest(new Point(200, 100), 0));
        Assert.False(forward.HitTest(new Point(200, 200), 0));
    }
}
