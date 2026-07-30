using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// Hides a region by blurring it.
///
/// <para>Samples the original capture, never the composite (§4.9) — see
/// <see cref="Annotation.Render"/> for why, and for the documented consequence that
/// an arrow underneath a blur is not blurred.</para>
///
/// <para><b>Three box passes, not a Gaussian.</b> §4.9 and this phase's plan called
/// for a cheap box blur during the drag with a separable Gaussian computed on a
/// background thread and swapped in on release. Three successive box blurs
/// <i>are</i> a Gaussian to within a few percent — that is the standard
/// approximation — and a running-sum box pass is O(pixels) with no dependence on
/// radius at all. Measured on a full-screen region it is a few milliseconds, so
/// there is no slow path to hide behind a fast one. That deletes the background
/// thread, the swap-in, the cancellation, the second quality level and the
/// interactive flag; what is left is one blur that is always the good one.</para>
/// </summary>
public sealed class BlurAnnotation : RectangleAnnotation
{
    /// <summary>Enough to destroy a word at ordinary screenshot scale.</summary>
    public const double DefaultRadius = 8;

    /// <summary>
    /// Three passes is where box stops looking like box. One leaves a visible
    /// square-edged smear that reads as a rendering fault; three is indistinguishable
    /// from a Gaussian without a difference image.
    /// </summary>
    private const int Passes = 3;

    public double Radius { get; set; } = DefaultRadius;

    public BlurAnnotation() =>
        // No stroke and no fill: an outline would draw a box around the thing being
        // hidden, which is a redaction pointing at itself.
        Style = new AnnotationStyle { StrokeWidth = 0 };

    /// <summary>
    /// Carried as geometry, not in <see cref="Annotation.Style"/>, for the same
    /// reason as a pixelate's block size: a number that is not a stroke width does
    /// not go in the field called stroke width.
    /// </summary>
    public override double SizeControl
    {
        get => Radius;
        set => Radius = value;
    }

    public override (double Min, double Max) SizeControlRange => (2, 40);

    public override string SizeControlLabel => "Blur radius";

    public override GeometryState CaptureGeometry() => new BlurGeometryState(Size, Radius);

    public override void RestoreGeometry(GeometryState state)
    {
        var blur = (BlurGeometryState)state;
        Size = blur.Size;
        Radius = blur.Radius;
    }

    public override GeometryState GeometryForBounds(Size size) => new BlurGeometryState(size, Radius);

    /// <summary>
    /// The last blur computed, and everything it depended on.
    ///
    /// <para>One immutable record behind one reference, rather than four fields.
    /// Reading a reference is atomic, so a caller can never see a result paired with
    /// the wrong region — which for a redaction is not a glitch, it is the wrong
    /// pixels shown to someone. (The save pipeline re-parses the document into its
    /// own object graph, so today nothing shares an annotation across threads; this
    /// costs nothing and does not depend on that staying true.)</para>
    ///
    /// <para><b>Held on the annotation</b>, which is the whole of §4.19's requirement
    /// that blur caches are dropped when the object is deleted or the document
    /// closes: they are, by the object going away. A central cache would need
    /// eviction, ownership and a lifetime, all to reach the same place.</para>
    /// </summary>
    private sealed record Cached(BitmapSource Source, Rect Region, double Radius, BitmapSource Result);

    private Cached? _cache;

    public override void Render(DrawingContext dc, BitmapSource source)
    {
        var region = RegionIn(source);
        if (region.IsEmpty) return;

        var blurred = Blurred(source, region);

        var inverse = Transform;
        if (!inverse.HasInverse) return;
        inverse.Invert();

        // Clipped in the object's own space, drawn back in image space — the space
        // it was sampled from. See PixelateAnnotation for the full reasoning; a blur
        // is rotationally symmetric enough that this matters less, but sampling and
        // drawing in the same space is what keeps it exact.
        dc.PushTransform(new MatrixTransform(Transform));
        dc.PushClip(new RectangleGeometry(LocalBounds));
        dc.PushTransform(new MatrixTransform(inverse));

        // Scaled back up to the region if it was shrunk to be blurred. WPF's default
        // smooth filtering is wanted here and is the opposite of what a pixelate
        // needs — the whole point of this tool is that there are no hard edges to
        // preserve.
        dc.DrawImage(blurred, region);

        dc.Pop();
        dc.Pop();
        dc.Pop();
    }

    /// <summary>
    /// The blurred patch, from cache when nothing it depends on has moved.
    ///
    /// <para><b>The region is part of the key, so moving recomputes.</b> Revision 1
    /// of §4.9 said recompute on resize but not on move, which drags a stale patch
    /// across the picture — for a redaction tool that means displaying the content it
    /// was placed there to hide, and it looks entirely convincing while doing so.
    /// That is the single worst failure available in this phase and the reason the
    /// key is what it is.</para>
    /// </summary>
    private BitmapSource Blurred(BitmapSource source, Rect region)
    {
        var cache = _cache;
        if (cache is not null
            && ReferenceEquals(cache.Source, source)
            && cache.Region == region
            && cache.Radius == Radius)
        {
            return cache.Result;
        }

        var result = Compute(source, region);
        _cache = new Cached(source, region, Radius, result);
        return result;
    }

    /// <summary>
    /// How much the patch is shrunk before blurring.
    ///
    /// <para><b>A blur is a low-pass filter, so the detail lost by shrinking first is
    /// detail the blur exists to destroy.</b> At radius 40 that is sixty-four times
    /// less work for a result nobody can pick out of a line-up, and it is what makes
    /// one always-good blur affordable instead of a cheap one during the drag and a
    /// real one on a background thread afterwards.</para>
    ///
    /// <para>Held back on small regions: shrinking a 40px redaction by eight leaves
    /// five pixels to blur, and the radius has nothing left to mean.</para>
    /// </summary>
    private static int Shrink(double radius, int width, int height)
    {
        var factor = (int)Math.Clamp(Math.Floor(radius / 4), 1, 8);
        while (factor > 1 && (width / factor < 16 || height / factor < 16)) factor--;
        return factor;
    }

    private BitmapSource Compute(BitmapSource source, Rect region)
    {
        var cropped = new CroppedBitmap(source, new Int32Rect(
            (int)region.X, (int)region.Y, (int)region.Width, (int)region.Height));

        var factor = Shrink(Radius, cropped.PixelWidth, cropped.PixelHeight);

        // Straight BGRA whatever came in. A capture that has been through
        // RenderTargetBitmap is premultiplied, and reading those bytes as straight
        // alpha darkens every translucent pixel.
        var patch = new FormatConvertedBitmap(
            factor == 1
                ? cropped
                : new TransformedBitmap(cropped, new ScaleTransform(1.0 / factor, 1.0 / factor)),
            PixelFormats.Bgra32, null, 0);

        var width = patch.PixelWidth;
        var height = patch.PixelHeight;
        var stride = width * 4;

        var pixels = new byte[stride * height];
        patch.CopyPixels(pixels, stride, 0);

        var scratch = new byte[pixels.Length];
        var radius = (int)Math.Max(1, Math.Round(Radius / factor));
        for (var pass = 0; pass < Passes; pass++)
        {
            // Across each row: neighbours are four bytes apart, rows are a stride apart.
            BoxPass(pixels, scratch, width, height, step: 4, laneStep: stride, radius);
            // Down each column: neighbours are a stride apart, columns four bytes apart.
            BoxPass(scratch, pixels, height, width, step: stride, laneStep: 4, radius);
        }

        var blurred = BitmapSource.Create(
            width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        blurred.Freeze();
        return blurred;
    }

    /// <summary>
    /// One horizontal box pass by running sum: each output pixel costs one add and
    /// one subtract regardless of radius, which is what makes a 40px blur no dearer
    /// than a 2px one and removes the reason to have two quality levels.
    /// </summary>
    /// <summary>
    /// One box pass along a row or a column, chosen by <paramref name="step"/>.
    ///
    /// <para>The two directions were separate methods and are one because the only
    /// difference is the stride between neighbours — four bytes across a row, a whole
    /// row down a column. Two copies of a running-sum loop is two places for an
    /// off-by-one that shows up as a one-pixel smear nobody looks for.</para>
    ///
    /// <para><b>Written for speed, which is the whole argument for having no second
    /// quality level.</b> Four accumulators as locals rather than a
    /// <c>Span&lt;int&gt;</c>, and the run split into three parts so the long middle
    /// clamps nothing — the first draft did <c>Math.Clamp</c> twice per pixel per
    /// channel and took 2.2 s on a full-screen 4K region, which would have made the
    /// background thread necessary after all.</para>
    /// </summary>
    private static void BoxPass(byte[] src, byte[] dst, int count, int lanes, int step, int laneStep, int radius)
    {
        var window = radius * 2 + 1;

        for (var lane = 0; lane < lanes; lane++)
        {
            var start = lane * laneStep;
            var last = start + (count - 1) * step;

            int s0 = 0, s1 = 0, s2 = 0, s3 = 0;

            // Primed over the whole window with the edge pixel repeated, so the
            // border tends toward the edge colour rather than toward transparent.
            for (var i = -radius; i <= radius; i++)
            {
                var p = i <= 0 ? start : i >= count ? last : start + i * step;
                s0 += src[p]; s1 += src[p + 1]; s2 += src[p + 2]; s3 += src[p + 3];
            }

            for (var i = 0; i < count; i++)
            {
                var o = start + i * step;
                dst[o] = (byte)(s0 / window);
                dst[o + 1] = (byte)(s1 / window);
                dst[o + 2] = (byte)(s2 / window);
                dst[o + 3] = (byte)(s3 / window);

                var leaveIndex = i - radius;
                var enterIndex = i + radius + 1;
                var leaving = leaveIndex <= 0 ? start : start + leaveIndex * step;
                var entering = enterIndex >= count ? last : start + enterIndex * step;

                s0 += src[entering] - src[leaving];
                s1 += src[entering + 1] - src[leaving + 1];
                s2 += src[entering + 2] - src[leaving + 2];
                s3 += src[entering + 3] - src[leaving + 3];
            }
        }
    }

    /// <summary>
    /// The patch of capture under this object, in whole pixels and inside the image.
    ///
    /// <para>Identical in intent to <see cref="PixelateAnnotation"/>'s: a shape
    /// dragged off the edge of the capture is ordinary, and <see cref="CroppedBitmap"/>
    /// throws on a rect that leaves the source.</para>
    /// </summary>
    private Rect RegionIn(BitmapSource source)
    {
        var image = new Rect(0, 0, source.PixelWidth, source.PixelHeight);

        var region = Bounds;
        region.Intersect(image);
        if (region.IsEmpty) return Rect.Empty;

        region = new Rect(
            Math.Floor(region.X), Math.Floor(region.Y),
            Math.Ceiling(region.Right) - Math.Floor(region.X),
            Math.Ceiling(region.Bottom) - Math.Floor(region.Y));
        region.Intersect(image);

        return region.Width < 1 || region.Height < 1 ? Rect.Empty : region;
    }
}
