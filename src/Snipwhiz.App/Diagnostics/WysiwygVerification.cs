using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.App.Editor;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Scene;

namespace Snipwhiz.App.Diagnostics;

/// <summary>
/// Proves that what the canvas shows and what the flattener exports are the same
/// pixels.
///
/// <para>Spec 2b §1 puts <c>Annotation.Render</c> in Core precisely so there is one
/// implementation. That is an architectural intention, not a guarantee — a future
/// tool could draw its selection state, its live preview, or its text differently
/// on screen than on export, and nothing else in the suite would notice. "The
/// export doesn't match what I drew" would then be a whole class of bug with no
/// check at all, discovered by a user after they had already sent the image.</para>
///
/// <para>The comparison renders <b>the visuals the canvas is actually displaying</b>
/// rather than building fresh ones. Comparing two fresh renders would only prove
/// the flattener agrees with itself.</para>
///
/// <code>
/// $env:SNIPWHIZ_VERIFY_WYSIWYG = "1"
/// dotnet run --project src/Snipwhiz.App
/// # result: %TEMP%\snipwhiz-wysiwyg-verify.txt
/// </code>
///
/// <para><b>NEGATIVE CONTROL</b> — shifts the flattened scene by one pixel, the
/// smallest divergence worth catching. The diff must be non-zero and must say
/// where:</para>
/// <code>
/// $env:SNIPWHIZ_VERIFY_BREAK_WYSIWYG = "1"
/// </code>
/// </summary>
internal static class WysiwygVerification
{
    private const int Width = 520;
    private const int Height = 360;

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_WYSIWYG") == "1";

    public static bool BreakWysiwyg =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_BREAK_WYSIWYG") == "1";

    private static readonly string ResultPath =
        Path.Combine(Path.GetTempPath(), "snipwhiz-wysiwyg-verify.txt");

    public static void RunIfRequested()
    {
        if (!IsEnabled) return;

        var canvas = new CanvasHost();
        var window = new Window
        {
            Title = "Snipwhiz WYSIWYG gate",
            Width = Width + 80,
            Height = Height + 120,
            Background = Brushes.Black,
            Content = canvas,
        };

        window.ContentRendered += (_, _) =>
        {
            var source = Source();
            var document = BuildScene();

            canvas.Load(source, document);
            var onScreen = canvas.RenderSceneAtImageScale();

            // Under the control the scene is nudged one pixel, standing in for a
            // flattener that draws differently — the point is whether the diff has
            // the resolution to notice, not to plant a fake bug in production code.
            //
            // Nudged in place, after the canvas has already rendered to a bitmap.
            // Building a copy meant reconstructing each annotation, which quietly
            // turned every subclass back into its base type — a highlight would have
            // been flattened as a plain rectangle and the control would still have
            // "passed" for the wrong reason.
            if (BreakWysiwyg) Nudge(document);
            var exported = Flattener.Render(source, document);

            Write(Compare(onScreen, exported), exported.PixelWidth, exported.PixelHeight);

            window.Close();
            Application.Current.Shutdown();
        };

        window.Show();
    }

    private readonly record struct Diff(int DifferingPixels, int MaxChannelDelta, int FirstX, int FirstY);

    private static Diff Compare(BitmapSource a, BitmapSource b)
    {
        if (a.PixelWidth != b.PixelWidth || a.PixelHeight != b.PixelHeight)
            return new Diff(-1, -1, -1, -1);

        var stride = a.PixelWidth * 4;
        var left = new byte[stride * a.PixelHeight];
        var right = new byte[stride * b.PixelHeight];
        a.CopyPixels(left, stride, 0);
        b.CopyPixels(right, stride, 0);

        var differing = 0;
        var worst = 0;
        var firstX = -1;
        var firstY = -1;

        for (var i = 0; i < left.Length; i += 4)
        {
            var delta = 0;
            for (var c = 0; c < 4; c++) delta = Math.Max(delta, Math.Abs(left[i + c] - right[i + c]));
            if (delta == 0) continue;

            differing++;
            worst = Math.Max(worst, delta);
            if (firstX < 0)
            {
                firstX = i / 4 % a.PixelWidth;
                firstY = i / 4 / a.PixelWidth;
            }
        }

        return new Diff(differing, worst, firstX, firstY);
    }

    /// <summary>A scene chosen to exercise the parts most likely to diverge.</summary>
    private static SceneDocument BuildScene()
    {
        var document = new SceneDocument { CaptureId = Guid.CreateVersion7() };

        // Cropped, and deliberately off-origin so a missing translate shows up as a
        // shift rather than as nothing at all. Several objects below straddle its
        // edge, which is what makes this a test of clipping and not merely of size:
        // the two paths have to agree about the half of a rectangle that is outside.
        // They agree by construction — both translate by the crop origin into a
        // crop-sized target and let the target's own edge do the clipping — and this
        // is what would say so if that ever became two mechanisms.
        document.Crop = new Rect(40, 30, 430, 300);

        // Plain outline.
        document.Annotations.Add(Rect(60, 50, 160, 90, 0,
            AnnotationStyle.Default with { StrokeWidth = 4 }));

        // Rotated, so the transform path is covered rather than only axis-aligned drawing.
        document.Annotations.Add(Rect(300, 90, 140, 70, 33,
            AnnotationStyle.Default with { Stroke = Colors.DodgerBlue, StrokeWidth = 6 }, z: 1));

        // Filled and translucent, overlapping the one below: compositing order and
        // alpha are where two render paths diverge most quietly.
        document.Annotations.Add(Rect(120, 190, 200, 120, 0,
            new AnnotationStyle { Stroke = Colors.White, StrokeWidth = 2, Fill = Colors.OrangeRed, Opacity = 0.55 }, z: 2));

        document.Annotations.Add(Rect(240, 240, 180, 90, -18,
            new AnnotationStyle { Stroke = Colors.Gold, StrokeWidth = 3, Fill = Colors.MediumPurple, Opacity = 0.4 }, z: 3));

        // A hairline, where anti-aliasing differences would show first.
        document.Annotations.Add(Rect(40, 320, 120, 20, 0,
            AnnotationStyle.Default with { Stroke = Colors.LimeGreen, StrokeWidth = 1 }, z: 4));

        // A highlight over the top of everything, at its shipping default. Task B3
        // traded §4.5's multiply blend for a translucent fill precisely so this gate
        // could cover it; an Effect would have rendered on screen and not in the
        // export, and this diff is what would have said so.
        var highlight = new HighlightAnnotation { ZIndex = 5 };
        highlight.Fit(new Point(150, 130), new Point(370, 174));
        document.Annotations.Add(highlight);

        // An arrow crossing the highlight: a filled head over a translucent fill is
        // the compositing case most likely to diverge between two render paths.
        var arrow = new ArrowAnnotation
        {
            ZIndex = 6,
            Style = AnnotationStyle.Default with { Stroke = Colors.White, StrokeWidth = 7 },
        };
        arrow.Fit(new Point(90, 250), new Point(330, 140));
        document.Annotations.Add(arrow);

        // A pixelate, rotated, straddling the crop edge and overlapping the objects
        // above it. This is the first annotation that samples the capture, and the
        // one place a second render path could reappear after phase B closed all the
        // others: if the canvas ever sampled what is on screen while the flattener
        // sampled the file, everything under this rectangle would differ and nothing
        // else in the suite would say so.
        var pixelate = new PixelateAnnotation { ZIndex = 7, BlockSize = 9 };
        pixelate.Fit(new Point(360, 210), new Point(500, 300));
        var turned = pixelate.Transform;
        turned.RotateAt(-12, turned.OffsetX, turned.OffsetY);
        pixelate.Transform = turned;
        document.Annotations.Add(pixelate);

        // A blur over the busiest part of the scene. It caches its result, which is
        // the one thing here that could make the canvas and the export disagree
        // without either of them being wrong on its own — two renders, two caches,
        // and only a diff to say whether they hold the same pixels.
        var blur = new BlurAnnotation { ZIndex = 8, Radius = 14 };
        blur.Fit(new Point(70, 60), new Point(210, 150));
        document.Annotations.Add(blur);

        // A spotlight over everything, at a deliberately mild dim. It is the only
        // annotation that paints most of the picture, so it covers whether a
        // full-canvas geometry lands identically in both paths — and a strong dim
        // here would flatten the channel deltas the control below depends on.
        // A magnifier aimed somewhere other than where it is drawn — the only object
        // here that reads the capture at one place and paints it at another, which
        // is a distinct way for two render paths to disagree.
        var magnify = new MagnifyAnnotation { ZIndex = 9, Zoom = 3 };
        magnify.Fit(new Point(300, 40), new Point(420, 130));
        magnify.SourceCentre = new Point(140, 300);
        document.Annotations.Add(magnify);

        var spotlight = new SpotlightAnnotation { ZIndex = 10 };
        spotlight.Fit(new Point(120, 100), new Point(400, 260));
        spotlight.SizeControl = 30;
        document.Annotations.Add(spotlight);

        return document;
    }

    private static RectangleAnnotation Rect(
        double cx, double cy, double w, double h, double degrees, AnnotationStyle style, int z = 0)
    {
        var transform = Matrix.Identity;
        transform.Rotate(degrees);
        transform.Translate(cx, cy);
        return new RectangleAnnotation { Size = new Size(w, h), Transform = transform, Style = style, ZIndex = z };
    }

    private static void Nudge(SceneDocument document)
    {
        foreach (var annotation in document.Annotations)
        {
            var moved = annotation.Transform;
            moved.OffsetX += 1;
            annotation.Transform = moved;
        }
    }

    /// <summary>A gradient, so a shifted or missing annotation cannot hide against a flat field.</summary>
    private static BitmapSource Source()
    {
        var stride = Width * 4;
        var pixels = new byte[stride * Height];
        for (var y = 0; y < Height; y++)
        {
            for (var x = 0; x < Width; x++)
            {
                var i = y * stride + x * 4;
                pixels[i] = (byte)(40 + x * 160 / Width);
                pixels[i + 1] = (byte)(30 + y * 120 / Height);
                pixels[i + 2] = 60;
                pixels[i + 3] = 255;
            }
        }
        var source = BitmapSource.Create(Width, Height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private static void Write(Diff diff, int width, int height)
    {
        var mode = BreakWysiwyg
            ? "NEGATIVE CONTROL (exported scene shifted one pixel)"
            : "POSITIVE (canvas visuals vs flattener)";

        var total = width * height;
        File.WriteAllText(ResultPath,
            $"mode={mode}\n" +
            $"capture={Width}x{Height}, compared over {width}x{height}   ({total:N0} pixels)\n" +
            "\n" +
            $"differingPixels={diff.DifferingPixels}\n" +
            $"maxChannelDelta={diff.MaxChannelDelta}\n" +
            $"firstDifference={(diff.FirstX < 0 ? "none" : $"({diff.FirstX},{diff.FirstY})")}\n" +
            "\n" +
            "Expected: differingPixels=0. The canvas and the flattener call the same\n" +
            "Annotation.Render, so anything above zero means a second render path has\n" +
            "appeared and the export no longer matches what the user drew.\n" +
            "\n" +
            "differingPixels=-1 means the two images are not even the same size.\n");
    }
}
