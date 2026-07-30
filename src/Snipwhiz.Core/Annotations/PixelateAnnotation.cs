using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// Hides a region by replacing it with blocks of its own average colour.
///
/// <para>The first annotation whose appearance depends on pixels it does not own,
/// and the reason <see cref="Annotation.Render"/> takes the capture at all. It is
/// deliberately the <b>cheapest</b> pixel tool — no cache, no background thread, no
/// second quality — so that the task introducing the signature is about the
/// signature. Blur brings all of that.</para>
///
/// <para>A rectangle underneath, because a pixelate is a box: size, bounds, drag-to-
/// create, resize and hit-testing are all already right, and the same inheritance
/// <see cref="HighlightAnnotation"/> uses.</para>
/// </summary>
public sealed class PixelateAnnotation : RectangleAnnotation
{
    /// <summary>Coarse enough to destroy a word, fine enough to keep the shape of one.</summary>
    public const double DefaultBlockSize = 12;

    public double BlockSize { get; set; } = DefaultBlockSize;

    public PixelateAnnotation() =>
        // No stroke and no fill: the blocks are the whole object. A stroke would
        // outline the thing being hidden, which is a redaction pointing at itself.
        Style = new AnnotationStyle { StrokeWidth = 0 };

    /// <summary>
    /// The pill's size control means block size here.
    ///
    /// <para>Carried as geometry rather than in <see cref="Annotation.Style"/> —
    /// which has a spare <c>StrokeWidth</c> this could have squatted in. That is
    /// precisely the borrowing that made a caption paint its plate in its own ink;
    /// a number that is not a stroke width does not go in the field called stroke
    /// width.</para>
    /// </summary>
    public override double SizeControl
    {
        get => BlockSize;
        set => BlockSize = value;
    }

    /// <summary>
    /// Two is the smallest block that hides anything; past about forty a redaction
    /// is one flat rectangle and the block size has stopped meaning anything.
    /// </summary>
    public override (double Min, double Max) SizeControlRange => (2, 40);

    public override string SizeControlLabel => "Block size";

    public override GeometryState CaptureGeometry() => new PixelateGeometryState(Size, BlockSize);

    public override void RestoreGeometry(GeometryState state)
    {
        var pixelate = (PixelateGeometryState)state;
        Size = pixelate.Size;
        BlockSize = pixelate.BlockSize;
    }

    public override GeometryState GeometryForBounds(Size size) =>
        new PixelateGeometryState(size, BlockSize);

    public override void Render(DrawingContext dc, BitmapSource source)
    {
        var region = RegionIn(source);
        if (region.IsEmpty) return;

        var inverse = Transform;
        if (!inverse.HasInverse) return;
        inverse.Invert();

        // The clip is turned with the object; the blocks are not. Pixels sampled
        // along the image's own axes and then rotated would land as a tumbled grid
        // of diamonds — a redaction that draws the eye instead of releasing it. So
        // the shape is clipped in its own space and the patch drawn back in image
        // space, which is also the space it was sampled from.
        dc.PushTransform(new MatrixTransform(Transform));
        dc.PushClip(new RectangleGeometry(LocalBounds));
        dc.PushTransform(new MatrixTransform(inverse));

        DrawBlocks(dc, source, region);

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    /// <summary>
    /// The patch of capture under this object, in whole pixels and inside the image.
    ///
    /// <para><see cref="CroppedBitmap"/> takes an <see cref="Int32Rect"/> and throws
    /// on one that leaves the source, and a shape dragged off the edge of the
    /// capture is an ordinary thing to do.</para>
    /// </summary>
    private Rect RegionIn(BitmapSource source)
    {
        var image = new Rect(0, 0, source.PixelWidth, source.PixelHeight);

        var region = Bounds;
        region.Intersect(image);
        if (region.IsEmpty) return Rect.Empty;

        // Outward to whole pixels, then back inside the image: rounding in and then
        // clamping can leave a rect one pixel wide, which scales to nothing.
        region = new Rect(
            Math.Floor(region.X), Math.Floor(region.Y),
            Math.Ceiling(region.Right) - Math.Floor(region.X),
            Math.Ceiling(region.Bottom) - Math.Floor(region.Y));
        region.Intersect(image);

        return region.Width < 1 || region.Height < 1 ? Rect.Empty : region;
    }

    /// <summary>
    /// Downscales the region to one pixel per block, then paints each of those
    /// pixels as a rectangle.
    ///
    /// <para><b>The averaging is WPF's; the enlarging is not.</b> Scaling back up
    /// with an <see cref="ImageBrush"/> was the obvious version and it does not
    /// work: <c>RenderOptions.BitmapScalingMode</c> is not honoured on a brush, so
    /// the blocks came back interpolated — flat to about two parts in 255, which
    /// looks right and is a soft blur wearing a pixelate's name. A tool for hiding
    /// things does not get to be approximately hard-edged.</para>
    ///
    /// <para>Block edges are rounded to whole image pixels so neighbours abut on a
    /// pixel boundary and WPF has no partial coverage to anti-alias, which would
    /// otherwise show as a faint grid over the redaction.</para>
    ///
    /// <para><b>Recomputed on every render, cached nowhere.</b> Deliberate for this
    /// task: caching by <c>(region, radius)</c> is task C3's, where blur needs it and
    /// where getting the invalidation wrong means displaying the pixels the tool was
    /// placed there to hide. A full-screen capture at the default block size is a few
    /// tens of thousands of rectangles into a retained visual, which is a redraw and
    /// not a frame budget.</para>
    /// </summary>
    private void DrawBlocks(DrawingContext dc, BitmapSource source, Rect region)
    {
        var patch = new CroppedBitmap(source, new Int32Rect(
            (int)region.X, (int)region.Y, (int)region.Width, (int)region.Height));

        // A block bigger than the region would scale it to nothing, which throws.
        // Clamped rather than refused: dragging a small pixelate is how you find out
        // the block size is too big, and it should degrade to one flat block.
        var scale = Math.Max(
            1.0 / Math.Max(1, BlockSize),
            1.0 / Math.Min(patch.PixelWidth, patch.PixelHeight));

        // Straight BGRA whatever came in. A capture that has been through
        // RenderTargetBitmap is premultiplied, and reading those bytes as straight
        // alpha darkens every translucent pixel — the same trap Flattener.Save
        // records against the PNG encoder.
        var small = new FormatConvertedBitmap(
            new TransformedBitmap(patch, new ScaleTransform(scale, scale)),
            PixelFormats.Bgra32, null, 0);

        var cols = small.PixelWidth;
        var rows = small.PixelHeight;
        var bytes = new byte[cols * rows * 4];
        small.CopyPixels(bytes, cols * 4, 0);

        for (var row = 0; row < rows; row++)
        {
            var top = region.Y + Math.Round(row * region.Height / rows);
            var bottom = region.Y + Math.Round((row + 1) * region.Height / rows);

            for (var col = 0; col < cols; col++)
            {
                var i = (row * cols + col) * 4;
                var brush = new SolidColorBrush(
                    Color.FromArgb(bytes[i + 3], bytes[i + 2], bytes[i + 1], bytes[i]));
                brush.Freeze();

                var left = region.X + Math.Round(col * region.Width / cols);
                var right = region.X + Math.Round((col + 1) * region.Width / cols);
                dc.DrawRectangle(brush, null, new Rect(left, top, right - left, bottom - top));
            }
        }
    }
}
