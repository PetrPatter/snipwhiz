using System.Windows;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;
using Xunit;

namespace Snipwhiz.Core.Tests.Annotations;

/// <summary>
/// Ellipse and line, and specifically the two ways they are <i>not</i> rectangles.
///
/// <para>An ellipse is not its bounding box, and a line is emphatically not its
/// bounding box — a diagonal one's box is mostly empty air. Both are the cases a
/// shared box implementation would get wrong while looking correct in every
/// screenshot.</para>
/// </summary>
public class ShapeTests
{
    private static EllipseAnnotation Ellipse(double w = 200, double h = 100) =>
        new() { Size = new Size(w, h), Style = AnnotationStyle.Default with { StrokeWidth = 0 } };

    private static LineAnnotation Line(Point from, Point to)
    {
        var line = new LineAnnotation { Style = AnnotationStyle.Default with { StrokeWidth = 2 } };
        line.Fit(from, to);
        return line;
    }

    // ---- ellipse ----------------------------------------------------------

    [Fact]
    public void An_ellipse_is_hit_at_its_centre_and_on_its_axes()
    {
        var ellipse = Ellipse();

        Assert.True(ellipse.HitTest(new Point(0, 0), 0));
        Assert.True(ellipse.HitTest(new Point(99, 0), 0));
        Assert.True(ellipse.HitTest(new Point(0, 49), 0));
    }

    /// <summary>
    /// THE ellipse test. The corner of the bounds is a long way outside the curve,
    /// and a bounds check selects a shape the user can plainly see they missed.
    /// </summary>
    [Fact]
    public void The_corner_of_an_ellipses_bounding_box_is_not_part_of_the_ellipse()
    {
        var ellipse = Ellipse(200, 100);
        var corner = new Point(95, 47);

        Assert.True(ellipse.Bounds.Contains(corner));
        Assert.False(ellipse.HitTest(corner, 0));
    }

    [Fact]
    public void A_rectangle_of_the_same_bounds_does_claim_that_corner()
    {
        // The contrast is the point: same bounds, different answer. If these two
        // ever agree here, the ellipse has become a rectangle that draws a curve.
        var corner = new Point(95, 47);
        var rectangle = new RectangleAnnotation
        {
            Size = new Size(200, 100),
            Style = AnnotationStyle.Default with { StrokeWidth = 0 },
        };

        Assert.True(rectangle.HitTest(corner, 0));
        Assert.False(Ellipse(200, 100).HitTest(corner, 0));
    }

    [Fact]
    public void A_rotated_ellipse_is_hit_along_its_own_axes()
    {
        var transform = Matrix.Identity;
        transform.Rotate(90);
        transform.Translate(300, 200);
        var ellipse = new EllipseAnnotation
        {
            Size = new Size(200, 100),
            Transform = transform,
            Style = AnnotationStyle.Default with { StrokeWidth = 0 },
        };

        // Turned a quarter, the long axis runs vertically.
        Assert.True(ellipse.HitTest(new Point(300, 290), 0));
        Assert.False(ellipse.HitTest(new Point(390, 200), 0));
    }

    // ---- line -------------------------------------------------------------

    [Fact]
    public void A_line_is_hit_along_its_length()
    {
        var line = Line(new Point(100, 100), new Point(300, 300));

        Assert.True(line.HitTest(new Point(200, 200), 0));
        Assert.True(line.HitTest(new Point(110, 110), 0));
        Assert.True(line.HitTest(new Point(290, 290), 0));
    }

    /// <summary>
    /// THE line test. A diagonal's bounding box is mostly empty, and treating the
    /// box as the shape makes every one of those pixels select it.
    /// </summary>
    [Fact]
    public void The_empty_corner_of_a_diagonal_lines_bounding_box_is_not_the_line()
    {
        var line = Line(new Point(100, 100), new Point(300, 300));
        var corner = new Point(290, 110);

        Assert.True(line.Bounds.Contains(corner));
        Assert.False(line.HitTest(corner, 0));
    }

    [Fact]
    public void A_line_does_not_extend_past_its_ends()
    {
        // Projection onto a segment has to be clamped, or the hit region is an
        // infinite line and clicking far off the canvas selects it.
        var line = Line(new Point(100, 100), new Point(300, 100));

        Assert.True(line.HitTest(new Point(300, 100), 0));
        Assert.False(line.HitTest(new Point(340, 100), 0));
    }

    [Fact]
    public void A_lines_bounds_span_its_two_ends_whichever_way_it_was_drawn()
    {
        var forward = Line(new Point(100, 50), new Point(300, 150));
        var backward = Line(new Point(300, 150), new Point(100, 50));

        Assert.Equal(forward.Bounds, backward.Bounds);
        Assert.Equal(new Rect(100, 50, 200, 100), forward.Bounds);
    }

    [Fact]
    public void Resizing_a_line_keeps_the_direction_it_was_drawn_in()
    {
        // Bounds are unsigned; a line's delta is not. Rebuilding it from a size
        // without carrying the signs flips the arrow end for end — which is the
        // kind of thing that only shows up once arrows exist.
        var upLeft = Line(new Point(300, 300), new Point(100, 100));
        Assert.True(upLeft.Delta.X < 0 && upLeft.Delta.Y < 0);

        var resized = (LineGeometryState)upLeft.GeometryForBounds(new Size(400, 400));

        Assert.True(resized.Delta.X < 0);
        Assert.True(resized.Delta.Y < 0);
        Assert.Equal(400, Math.Abs(resized.Delta.X));
    }

    [Fact]
    public void An_axis_aligned_line_keeps_both_extents_when_resized()
    {
        // Math.Sign returns zero for a perfectly horizontal line, which would
        // collapse the other axis to nothing.
        var horizontal = Line(new Point(100, 100), new Point(300, 100));

        var resized = (LineGeometryState)horizontal.GeometryForBounds(new Size(500, 0));

        Assert.Equal(500, resized.Delta.X);
        Assert.Equal(0, resized.Delta.Y);
    }

    [Fact]
    public void A_zero_length_line_is_still_hit_near_its_point()
    {
        var dot = Line(new Point(200, 200), new Point(200, 200));

        Assert.True(dot.HitTest(new Point(200, 200), 0));
        Assert.False(dot.HitTest(new Point(230, 200), 0));
    }

    // ---- shared model -----------------------------------------------------

    [Fact]
    public void Every_shape_reports_a_geometry_for_bounds_of_its_own_type()
    {
        // The selection tool asks each shape what its bounds mean. Answering with
        // another shape's state throws on the next resize.
        Assert.IsType<RectangleGeometryState>(
            new RectangleAnnotation().GeometryForBounds(new Size(10, 10)));
        Assert.IsType<EllipseGeometryState>(
            new EllipseAnnotation().GeometryForBounds(new Size(10, 10)));
        Assert.IsType<LineGeometryState>(
            new LineAnnotation().GeometryForBounds(new Size(10, 10)));
    }

    [Fact]
    public void Capture_and_restore_round_trip_for_every_shape()
    {
        var ellipse = Ellipse(200, 100);
        var state = ellipse.CaptureGeometry();
        ellipse.RestoreGeometry(new EllipseGeometryState(new Size(1, 1)));
        ellipse.RestoreGeometry(state);
        Assert.Equal(new Size(200, 100), ellipse.Size);

        var line = Line(new Point(0, 0), new Point(120, 40));
        var lineState = line.CaptureGeometry();
        line.RestoreGeometry(new LineGeometryState(new Vector(1, 1)));
        line.RestoreGeometry(lineState);
        Assert.Equal(new Vector(120, 40), line.Delta);
    }
}
