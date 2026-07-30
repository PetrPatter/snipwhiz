using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Scene;
using Xunit;

namespace Snipwhiz.Core.Tests.Annotations;

/// <summary>
/// Text plus a tail, and the first object with a grab point that is neither a
/// resize handle nor the rotate handle.
///
/// <para>Two things are being checked here and they are different in kind. One is
/// that the tail behaves: it points where it is put, it survives a move, a resize
/// and a save. The other is that <b>nothing about text had to be reimplemented</b> —
/// if the measuring, the editing overlay or the metrics seam had needed a second
/// version for callouts, that would have been a finding about B5's design rather
/// than about this tool.</para>
/// </summary>
public class CalloutTests
{
    private const int Size = 120;

    private static CalloutAnnotation Callout(string text = "Look here")
    {
        var callout = new CalloutAnnotation { Text = text, FontSize = 20 };
        callout.Fit(new Point(60, 40), new Point(60, 40));
        return callout;
    }

    private static byte[] Render(params Annotation[] annotations) => Sta.Run(() =>
    {
        var stride = Size * 4;
        var pixels = new byte[stride * Size];
        Array.Fill(pixels, (byte)255);
        var white = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);

        var rendered = Flattener.Render(
            white, new SceneDocument { CaptureId = Guid.Empty, Annotations = [.. annotations] });

        var read = new byte[stride * Size];
        rendered.CopyPixels(read, stride, 0);
        return read;
    });

    private static bool Inked(byte[] pixels, Point at) =>
        pixels[((int)at.Y * Size + (int)at.X) * 4] < 200;

    // ---- the tail ---------------------------------------------------------

    [Fact]
    public void The_tail_is_a_control_point_and_not_a_resizer()
    {
        var callout = Callout();

        Assert.Equal([HandleKind.Tail], callout.ControlPoints);
        Assert.DoesNotContain(HandleKind.Tail, Handles.Resizers);
    }

    [Fact]
    public void The_tail_handle_sits_at_the_tip()
    {
        var callout = Callout();
        callout.Tail = new Vector(30, 45);

        Assert.Equal(new Point(30, 45), Handles.LocalPosition(callout, HandleKind.Tail, rotateGap: 0));
        // And in image space it rides the bubble's transform, like every other handle.
        Assert.Equal(new Point(90, 85), Handles.ImagePosition(callout, HandleKind.Tail, rotateGap: 0));
    }

    [Fact]
    public void Dragging_the_tail_re_aims_it_without_moving_the_bubble()
    {
        var callout = Callout();
        var transform = callout.Transform;
        var bounds = callout.LocalBounds;

        var moved = callout.MoveControlPoint(HandleKind.Tail, new Point(-25, -60));
        Assert.NotNull(moved);
        callout.RestoreGeometry(moved);

        Assert.Equal(new Vector(-25, -60), callout.Tail);
        Assert.Equal(transform, callout.Transform);
        Assert.Equal(bounds, callout.LocalBounds);
    }

    /// <summary>
    /// Everything else asked for a control point gets nothing, which is what keeps
    /// the selection tool from having to know what a tail is.
    /// </summary>
    [Fact]
    public void An_ordinary_shape_has_no_control_points()
    {
        var rectangle = new RectangleAnnotation { Size = new Size(40, 30) };

        Assert.Empty(rectangle.ControlPoints);
        Assert.Null(rectangle.MoveControlPoint(HandleKind.Tail, new Point(10, 10)));
    }

    /// <summary>
    /// The tail belongs to the callout, unlike a magnifier's subject which belongs to
    /// the picture. Moving the bubble carries it.
    /// </summary>
    [Fact]
    public void Moving_the_bubble_carries_the_tail()
    {
        var callout = Callout();
        callout.Tail = new Vector(0, 50);

        var moved = callout.Transform;
        moved.OffsetX += 20;
        callout.Transform = moved;

        Assert.Equal(new Vector(0, 50), callout.Tail);
        Assert.Equal(80, Handles.ImagePosition(callout, HandleKind.Tail, 0).X);
    }

    [Fact]
    public void Resizing_keeps_the_tail()
    {
        var callout = Callout();
        callout.Tail = new Vector(-30, 40);

        callout.RestoreGeometry(callout.GeometryForBounds(new Size(200, 90)));

        Assert.Equal(new Vector(-30, 40), callout.Tail);
        Assert.True(callout.FontSize > 20, "resizing should still change the font size");
    }

    /// <summary>
    /// The one above was not enough, and a hand check found it. It asserts the tail
    /// <i>survives</i> a resize without asserting that it still aims anywhere in
    /// particular — and dragging a corner anchors the opposite corner, which slides
    /// the bubble's centre. The tail is measured from that centre, so it rode along
    /// and stopped pointing at what it had been pointing at.
    ///
    /// <para>This models the whole gesture the way <c>SelectTool</c> performs it,
    /// which is what the first test skipped: it called <c>GeometryForBounds</c> alone
    /// and never applied the transform that came with it.</para>
    /// </summary>
    [Fact]
    public void Resizing_from_a_corner_leaves_the_tip_where_it_was_pointing()
    {
        var callout = Callout();
        callout.Tail = new Vector(0, 60);
        var before = Handles.ImagePosition(callout, HandleKind.Tail, rotateGap: 0);

        // Several frames, because a real drag is many and this correction is measured
        // from the start of the gesture. Applied per frame to the previous frame's
        // result it compounds, and the tail accelerates off across the picture —
        // which is what the one-frame version of this test could not see.
        var after = Handles.ImagePosition(
            Resize(callout, HandleKind.TopLeft, frames:
                [new Point(-60, -30), new Point(-90, -45), new Point(-120, -60)]),
            HandleKind.Tail, rotateGap: 0);
        Assert.Equal(before.X, after.X, precision: 6);
        Assert.Equal(before.Y, after.Y, precision: 6);
    }

    /// <summary>
    /// Resizing about the centre moves no origin, so there is nothing to rebase and
    /// the tail must not be nudged by a correction that does not apply.
    /// </summary>
    [Fact]
    public void Resizing_about_the_centre_leaves_the_tail_alone()
    {
        var callout = Callout();
        callout.Tail = new Vector(0, 60);

        Resize(callout, HandleKind.TopLeft, aboutCentre: true, frames:
            [new Point(-60, -30), new Point(-120, -60)]);

        Assert.Equal(new Vector(0, 60), callout.Tail);
    }

    /// <summary>
    /// The anchor has to hold for a caption too, and it did not.
    ///
    /// <para><c>Handles.Resize</c> positions an object assuming its bounds will be
    /// the size it was asked for. A callout answers a resize by changing font size,
    /// so its width then comes from the glyphs and lands somewhere else — the anchor
    /// was computed against bounds the object never had, and the bubble skated around
    /// under the pointer. Reported by hand, after the tail fix.</para>
    /// </summary>
    [Theory]
    [InlineData(HandleKind.TopLeft)]
    [InlineData(HandleKind.TopRight)]
    [InlineData(HandleKind.BottomLeft)]
    [InlineData(HandleKind.BottomRight)]
    public void The_opposite_corner_stays_put_while_a_callout_is_resized(HandleKind handle)
    {
        var callout = Callout("A caption of some length");
        var opposite = Handles.Opposite(handle);
        var anchor = Handles.ImagePosition(callout, opposite, rotateGap: 0);

        Resize(callout, handle, frames:
            [new Point(-90, -60), new Point(-150, -100), new Point(-200, -140)]);

        var after = Handles.ImagePosition(callout, opposite, rotateGap: 0);
        Assert.Equal(anchor.X, after.X, precision: 6);
        Assert.Equal(anchor.Y, after.Y, precision: 6);
    }

    /// <summary>
    /// The whole resize gesture as <c>SelectTool</c> performs it, which is the part
    /// the first version of these tests skipped: it called <c>GeometryForBounds</c>
    /// alone and never applied the transform that comes with it, so nothing it
    /// asserted could see the object move.
    /// </summary>
    /// <summary>
    /// A drag, as <c>SelectTool</c> performs it: the start transform, start geometry
    /// and anchor are captured <b>once</b> and every frame is computed against them.
    /// </summary>
    private static Annotation Resize(
        Annotation annotation, HandleKind handle, bool aboutCentre = false, params Point[] frames)
    {
        var startTransform = annotation.Transform;
        var startGeometry = annotation.CaptureGeometry();
        var anchor = Handles.ImagePosition(annotation, Handles.Opposite(handle), 0);

        foreach (var local in frames)
        {
            var resized = Handles.Resize(annotation, handle, local, aboutCentre: aboutCentre);
            var (_, transform) = Handles.Settle(
                annotation, startTransform, startGeometry, resized, handle, anchor, aboutCentre);

            annotation.Transform = transform;
        }

        return annotation;
    }

    /// <summary>
    /// And nothing else is disturbed by the rebase: a shape's geometry is an extent
    /// and does not care where its centre went.
    /// </summary>
    [Fact]
    public void Rebasing_does_nothing_to_an_ordinary_shape()
    {
        var rectangle = new RectangleAnnotation { Size = new Size(40, 30) };
        var state = rectangle.GeometryForBounds(new Size(80, 60));

        Assert.Same(state, rectangle.Rebased(state, rectangle.CaptureGeometry(), new Vector(17, -9)));
    }

    [Fact]
    public void The_tail_can_be_grabbed()
    {
        var callout = Callout();
        callout.Tail = new Vector(0, 70);

        // Half way down the tail, in image space: the bubble is at (60,40).
        Assert.True(callout.HitTest(new Point(60, 75), tolerance: 2));
        // And well off to the side of it is still a miss.
        Assert.False(callout.HitTest(new Point(110, 75), tolerance: 2));
    }

    [Fact]
    public void The_tail_is_drawn()
    {
        var callout = Callout();
        callout.Tail = new Vector(0, 50);

        var pixels = Render(callout);

        // Near the tip, well below the bubble.
        Assert.True(Inked(pixels, new Point(60, 84)), "nothing was drawn at the tail tip");
    }

    /// <summary>
    /// The plate and the tail are one geometry rather than two overlapping fills.
    /// With a translucent plate — the shipping default — two fills would show as a
    /// darker patch where they overlap.
    /// </summary>
    [Fact]
    public void The_bubble_and_tail_do_not_double_darken_where_they_meet()
    {
        var callout = Callout();
        callout.Tail = new Vector(0, 60);
        callout.Style = TextAnnotation.DefaultStyle with { Fill = Color.FromArgb(0x80, 0, 0, 0) };

        var pixels = Render(callout);

        // Inside the bubble, away from glyphs, versus the seam where the tail leaves
        // it. A group of two translucent fills differs here; a union does not.
        var insideBubble = pixels[((int)28 * Size + 20) * 4];
        var atTheSeam = pixels[((int)52 * Size + 60) * 4];

        Assert.Equal(insideBubble, atTheSeam);
    }

    // ---- what it inherits -------------------------------------------------

    [Fact]
    public void It_is_a_text_annotation_so_the_editing_overlay_takes_it()
    {
        var callout = Callout();

        // Not a style point: TextEditingOverlay.Begin and EditorView's "created is
        // TextAnnotation" both take the base type, and this is what makes a callout
        // go straight into typing when it is placed.
        Assert.IsAssignableFrom<TextAnnotation>(callout);
    }

    [Fact]
    public void The_bubble_is_measured_from_the_words_like_any_caption()
    {
        var shortOne = Callout("Hi");
        var longOne = Callout("A considerably longer caption");

        Assert.True(longOne.LocalBounds.Width > shortOne.LocalBounds.Width);
    }

    [Fact]
    public void The_size_control_is_still_the_font_size()
    {
        var callout = Callout();

        callout.SizeControl = 44;

        Assert.Equal(44, callout.FontSize);
        Assert.Equal("Font size", callout.SizeControlLabel);
    }

    /// <summary>
    /// A callout is text, so its fill is the bubble behind the words and recolouring
    /// must not paint it in the ink colour — the bug that made a caption vanish in
    /// B5, inherited along with everything else.
    /// </summary>
    [Fact]
    public void Recolouring_a_callout_never_makes_its_bubble_match_its_ink()
    {
        var callout = Callout();

        var recoloured = callout.Recoloured(Colors.Red);

        Assert.Equal(Colors.Red, recoloured.Stroke);
        Assert.NotEqual(recoloured.Stroke, recoloured.Fill);
    }
}
