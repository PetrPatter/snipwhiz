using System.Windows;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;
using Xunit;

namespace Snipwhiz.Core.Tests.Annotations;

/// <summary>
/// Resize and rotate maths, proven before a mouse is involved.
///
/// <para>The case that matters is a <b>rotated</b> object: its handles move along
/// its own axes, not the screen's, and the anchor is the corner opposite the one
/// being dragged. Getting that wrong looks like the shape sliding away as you
/// resize it, and it is invisible on an unrotated rectangle — which is why the
/// unrotated tests here are the easy half, not the check.</para>
/// </summary>
public class HandleTests
{
    private static RectangleAnnotation Rect(double w = 200, double h = 100, double degrees = 0,
                                            double cx = 0, double cy = 0)
    {
        var transform = Matrix.Identity;
        transform.Rotate(degrees);
        transform.Translate(cx, cy);
        return new RectangleAnnotation { Size = new Size(w, h), Transform = transform };
    }

    private static RectangleAnnotation Apply(RectangleAnnotation source, Handles.Resized resized) =>
        new() { Size = resized.Size, Transform = resized.Transform, Style = source.Style };

    private static void AssertClose(Point expected, Point actual, double tolerance = 1e-6)
    {
        Assert.Equal(expected.X, actual.X, tolerance);
        Assert.Equal(expected.Y, actual.Y, tolerance);
    }

    // ---- where handles are ------------------------------------------------

    [Fact]
    public void Handles_sit_on_the_corners_and_edge_midpoints()
    {
        var rect = Rect(200, 100);

        AssertClose(new Point(-100, -50), Handles.LocalPosition(rect, HandleKind.TopLeft, 0));
        AssertClose(new Point(100, 50), Handles.LocalPosition(rect, HandleKind.BottomRight, 0));
        AssertClose(new Point(0, -50), Handles.LocalPosition(rect, HandleKind.Top, 0));
        AssertClose(new Point(100, 0), Handles.LocalPosition(rect, HandleKind.Right, 0));
    }

    [Fact]
    public void The_rotate_handle_sits_above_the_top_edge()
    {
        var rect = Rect(200, 100);
        AssertClose(new Point(0, -74), Handles.LocalPosition(rect, HandleKind.Rotate, rotateGap: 24));
    }

    [Fact]
    public void Handles_of_a_rotated_object_follow_the_rotation()
    {
        // Turned 90°, the top-left corner ends up at the top-right of the screen.
        var rect = Rect(200, 100, degrees: 90, cx: 500, cy: 300);

        AssertClose(new Point(550, 200), Handles.ImagePosition(rect, HandleKind.TopLeft, 0), 1e-9);
        AssertClose(new Point(450, 400), Handles.ImagePosition(rect, HandleKind.BottomRight, 0), 1e-9);
    }

    // ---- resize -----------------------------------------------------------

    [Fact]
    public void Dragging_a_corner_leaves_the_opposite_one_where_it_was()
    {
        var rect = Rect(200, 100, cx: 500, cy: 300);
        var anchor = Handles.ImagePosition(rect, HandleKind.TopLeft, 0);

        // Drag the bottom-right corner out to local (200, 150).
        var resized = Apply(rect, Handles.Resize(rect, HandleKind.BottomRight, new Point(200, 150)));

        Assert.Equal(new Size(300, 200), resized.Size);
        AssertClose(anchor, Handles.ImagePosition(resized, HandleKind.TopLeft, 0));
    }

    /// <summary>
    /// The one that matters. On a rotated object the anchor is only fixed if the
    /// new centre is computed in local space and pushed back through the transform.
    /// Computing it in image space — the obvious thing — slides the shape.
    /// </summary>
    [Theory]
    [InlineData(30)]
    [InlineData(45)]
    [InlineData(120)]
    [InlineData(-70)]
    public void Dragging_a_corner_of_a_rotated_object_leaves_the_opposite_one_where_it_was(double degrees)
    {
        var rect = Rect(200, 100, degrees, cx: 500, cy: 300);
        var anchor = Handles.ImagePosition(rect, HandleKind.TopLeft, 0);

        var resized = Apply(rect, Handles.Resize(rect, HandleKind.BottomRight, new Point(180, 130)));

        Assert.Equal(new Size(280, 180), resized.Size);
        AssertClose(anchor, Handles.ImagePosition(resized, HandleKind.TopLeft, 0), 1e-9);
    }

    [Fact]
    public void Every_handle_of_a_rotated_object_anchors_its_opposite()
    {
        // All eight, so no single case passes by symmetry.
        foreach (var kind in Handles.Resizers)
        {
            var rect = Rect(200, 100, degrees: 37, cx: 400, cy: 250);
            var opposite = Handles.Opposite(kind);
            var anchor = Handles.ImagePosition(rect, opposite, 0);

            var local = Handles.LocalPosition(rect, kind, 0);
            var dragged = new Point(local.X * 1.6, local.Y * 1.6);
            var resized = Apply(rect, Handles.Resize(rect, kind, dragged));

            AssertClose(anchor, Handles.ImagePosition(resized, opposite, 0), 1e-9);
        }
    }

    [Fact]
    public void An_edge_handle_changes_one_axis_and_leaves_the_other()
    {
        var rect = Rect(200, 100);

        var wider = Handles.Resize(rect, HandleKind.Right, new Point(250, 999));
        Assert.Equal(350, wider.Size.Width);
        Assert.Equal(100, wider.Size.Height);

        var taller = Handles.Resize(rect, HandleKind.Bottom, new Point(999, 80));
        Assert.Equal(200, taller.Size.Width);
        Assert.Equal(130, taller.Size.Height);
    }

    [Fact]
    public void Alt_resizes_about_the_centre_and_leaves_it_where_it_was()
    {
        var rect = Rect(200, 100, degrees: 25, cx: 300, cy: 200);
        var centre = new Point(rect.Transform.OffsetX, rect.Transform.OffsetY);

        var resized = Apply(rect, Handles.Resize(
            rect, HandleKind.BottomRight, new Point(150, 90), aboutCentre: true));

        Assert.Equal(new Size(300, 180), resized.Size);
        AssertClose(centre, new Point(resized.Transform.OffsetX, resized.Transform.OffsetY));
    }

    [Fact]
    public void Shift_keeps_the_original_proportions()
    {
        var rect = Rect(200, 100);   // 2:1

        var resized = Handles.Resize(
            rect, HandleKind.BottomRight, new Point(300, 110), preserveAspect: true);

        Assert.Equal(2, resized.Size.Width / resized.Size.Height, 6);
    }

    [Fact]
    public void Shift_still_anchors_the_opposite_corner()
    {
        var rect = Rect(200, 100, degrees: 40, cx: 500, cy: 300);
        var anchor = Handles.ImagePosition(rect, HandleKind.TopLeft, 0);

        var resized = Apply(rect, Handles.Resize(
            rect, HandleKind.BottomRight, new Point(300, 110), preserveAspect: true));

        AssertClose(anchor, Handles.ImagePosition(resized, HandleKind.TopLeft, 0), 1e-9);
    }

    [Fact]
    public void A_shape_dragged_inside_out_stops_at_a_minimum_rather_than_inverting()
    {
        var rect = Rect(200, 100);

        // Pulled far past the anchor, which would give a negative extent.
        var resized = Handles.Resize(rect, HandleKind.BottomRight, new Point(-500, -400), minimum: 4);

        Assert.True(resized.Size.Width >= 4);
        Assert.True(resized.Size.Height >= 4);
    }

    [Fact]
    public void A_zero_drag_cannot_produce_an_ungrabbable_shape()
    {
        var rect = Rect(200, 100);
        var resized = Handles.Resize(rect, HandleKind.BottomRight, new Point(-100, -50), minimum: 4);

        Assert.Equal(4, resized.Size.Width);
        Assert.Equal(4, resized.Size.Height);
    }

    // ---- rotate -----------------------------------------------------------

    [Fact]
    public void Pointing_straight_up_from_the_centre_is_zero_degrees()
    {
        var rect = Rect(200, 100, degrees: 133, cx: 400, cy: 400);

        var rotated = Handles.RotateToward(rect, new Point(400, 100));

        Assert.Equal(1, rotated.M11, 6);
        Assert.Equal(0, rotated.M12, 6);
    }

    [Fact]
    public void Pointing_right_is_a_quarter_turn()
    {
        var rect = Rect(200, 100, cx: 400, cy: 400);

        var rotated = Handles.RotateToward(rect, new Point(700, 400));

        // 90°: M11 = cos = 0, M12 = sin = 1.
        Assert.Equal(0, rotated.M11, 6);
        Assert.Equal(1, rotated.M12, 6);
    }

    [Fact]
    public void Rotation_keeps_the_object_centred_where_it_was()
    {
        var rect = Rect(200, 100, cx: 640, cy: 480);

        var rotated = Handles.RotateToward(rect, new Point(700, 300));

        Assert.Equal(640, rotated.OffsetX, 6);
        Assert.Equal(480, rotated.OffsetY, 6);
    }

    [Fact]
    public void Shift_snaps_rotation_to_fifteen_degree_steps()
    {
        var rect = Rect(200, 100, cx: 0, cy: 0);

        // 20° off vertical snaps to 15°.
        var target = new Point(Math.Sin(20 * Math.PI / 180) * 100, -Math.Cos(20 * Math.PI / 180) * 100);
        var rotated = Handles.RotateToward(rect, target, snapDegrees: 15);

        Assert.Equal(Math.Cos(15 * Math.PI / 180), rotated.M11, 6);
    }

    [Fact]
    public void Rotating_onto_the_centre_is_ignored_rather_than_undefined()
    {
        var rect = Rect(200, 100, degrees: 33, cx: 400, cy: 400);

        var rotated = Handles.RotateToward(rect, new Point(400, 400));

        Assert.Equal(rect.Transform, rotated);
    }
}
