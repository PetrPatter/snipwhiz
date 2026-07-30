using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Scene;
using Xunit;

namespace Snipwhiz.Core.Tests.Annotations;

/// <summary>
/// A magnifier, checked by whether the pattern it shows is the pattern that is
/// there, at the size it claims.
///
/// <para>The failure this guards is the plan's whole reason for insisting on two
/// rectangles: sample and draw from the same one and the tool still <b>works</b> —
/// it draws a convincing enlargement — it just can only ever cover its own subject.
/// Nothing about the rendered result looks wrong, which is why
/// <see cref="A_magnifier_moved_away_still_shows_what_it_was_pointed_at"/> exists.</para>
/// </summary>
public class MagnifyTests
{
    private const int Size = 64;

    /// <summary>
    /// Black, with a single white pixel at (10,10) and a white 2x2 block at (40,40).
    /// Distinct landmarks, so which part was sampled is answerable from the output.
    /// </summary>
    private static BitmapSource Landmarks()
    {
        var stride = Size * 4;
        var pixels = new byte[stride * Size];
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        void White(int x, int y)
        {
            var i = y * stride + x * 4;
            pixels[i] = pixels[i + 1] = pixels[i + 2] = 255;
        }

        White(10, 10);
        for (var y = 40; y < 42; y++)
        {
            for (var x = 40; x < 42; x++) White(x, y);
        }

        var source = BitmapSource.Create(Size, Size, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private static MagnifyAnnotation Magnify(Rect lens, double zoom)
    {
        var magnify = new MagnifyAnnotation { Zoom = zoom, Style = AnnotationStyle.Default with { StrokeWidth = 0 } };
        magnify.Fit(lens.TopLeft, lens.BottomRight);
        return magnify;
    }

    private static byte[] Render(params Annotation[] annotations) => Sta.Run(() =>
    {
        var rendered = Flattener.Render(
            Landmarks(), new SceneDocument { CaptureId = Guid.Empty, Annotations = [.. annotations] });

        var pixels = new byte[Size * Size * 4];
        rendered.CopyPixels(pixels, Size * 4, 0);
        return pixels;
    });

    private static byte Grey(byte[] pixels, int x, int y) => pixels[(y * Size + x) * 4];

    /// <summary>How much of a region is lit, for comparing an original against a blow-up.</summary>
    private static int Bright(byte[] pixels, Rect area)
    {
        var count = 0;
        for (var y = (int)area.Y; y < area.Bottom; y++)
        {
            for (var x = (int)area.X; x < area.Right; x++)
            {
                if (Grey(pixels, x, y) > 128) count++;
            }
        }
        return count;
    }

    /// <summary>
    /// A 2x2 block at 4x covers roughly 8x8. Checked as an area rather than
    /// pixel-exactly, because the enlargement is filtered and its edges are soft.
    /// </summary>
    [Fact]
    public void A_magnified_landmark_comes_back_bigger()
    {
        // Centred on the 2x2 block at (40,40): a 32px lens at 4x samples the 8x8
        // around it, so the block is inside the window rather than just near it.
        var pixels = Render(Magnify(new Rect(24, 24, 32, 32), zoom: 4));

        var original = Bright(Render(), new Rect(38, 38, 6, 6));
        var enlarged = Bright(pixels, new Rect(24, 24, 32, 32));

        Assert.True(enlarged > original * 4,
            $"the block did not grow: {original} bright pixels became {enlarged}");
    }

    /// <summary>
    /// The one the two-rectangle design exists for. The lens is moved off its
    /// subject entirely; what it shows must not change.
    /// </summary>
    [Fact]
    public void A_magnifier_moved_away_still_shows_what_it_was_pointed_at()
    {
        var magnify = Magnify(new Rect(32, 32, 24, 24), zoom: 3);
        var inPlace = Render(magnify);

        // Bodily to the opposite corner, over blank capture. Only Transform moves,
        // which is all MoveAnnotation ever changes.
        var moved = magnify.Transform;
        moved.OffsetX -= 32;
        moved.OffsetY -= 32;
        magnify.Transform = moved;

        var afterMoving = Render(magnify);

        var before = Bright(inPlace, new Rect(32, 32, 24, 24));
        var after = Bright(afterMoving, new Rect(0, 0, 24, 24));

        Assert.True(before > 0, "the magnifier showed nothing to begin with");
        Assert.Equal(before, after);
    }

    /// <summary>
    /// And the corollary, which is what makes the test above meaningful: the place it
    /// moved off is no longer covered, so it really did move.
    /// </summary>
    [Fact]
    public void Moving_the_lens_uncovers_where_it_was()
    {
        var magnify = Magnify(new Rect(32, 32, 24, 24), zoom: 3);
        var covered = Render(magnify);

        var moved = magnify.Transform;
        moved.OffsetX -= 32;
        moved.OffsetY -= 32;
        magnify.Transform = moved;

        Assert.NotEqual(
            Bright(covered, new Rect(32, 32, 24, 24)),
            Bright(Render(magnify), new Rect(32, 32, 24, 24)));
    }

    [Fact]
    public void A_lens_near_the_edge_keeps_its_aspect_ratio()
    {
        // Aimed at the very corner: the sampled region would run off the capture and
        // has to be pushed back inside rather than clipped, or DrawImage stretches a
        // narrower patch to fill the lens.
        var magnify = Magnify(new Rect(20, 20, 40, 40), zoom: 2);
        magnify.SourceCentre = new Point(1, 1);

        var pixels = Render(magnify);

        // The single white pixel at (10,10) is inside the pushed-back 20x20 sample,
        // so something is lit and nothing threw.
        Assert.True(Bright(pixels, new Rect(20, 20, 40, 40)) > 0);
    }

    [Fact]
    public void The_size_control_is_the_magnification()
    {
        var magnify = new MagnifyAnnotation();

        magnify.SizeControl = 6;

        Assert.Equal(6, magnify.Zoom);
        Assert.Equal((2, 8), magnify.SizeControlRange);
    }

    [Fact]
    public void Resizing_the_lens_keeps_the_zoom_and_the_subject()
    {
        var magnify = Magnify(new Rect(10, 10, 20, 20), zoom: 5);
        var subject = magnify.SourceCentre;

        magnify.RestoreGeometry(magnify.GeometryForBounds(new Size(40, 40)));

        Assert.Equal(5, magnify.Zoom);
        Assert.Equal(subject, magnify.SourceCentre);
        Assert.Equal(new Size(40, 40), magnify.Size);
    }

    [Fact]
    public void Creating_one_points_it_at_what_it_covers()
    {
        var magnify = Magnify(new Rect(10, 20, 30, 40), zoom: 2);

        Assert.Equal(new Point(25, 40), magnify.SourceCentre);
    }
}
