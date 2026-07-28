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

            // The scene the flattener is given. Under the control it is nudged one
            // pixel, standing in for a flattener that draws differently — the point
            // is whether the diff has the resolution to notice, not to plant a fake
            // bug in production code.
            var exported = Flattener.Render(source, BreakWysiwyg ? Nudged(document) : document);

            Write(Compare(onScreen, exported));

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

    private static SceneDocument Nudged(SceneDocument document)
    {
        var copy = new SceneDocument { CaptureId = document.CaptureId };
        foreach (var annotation in document.Annotations.OfType<RectangleAnnotation>())
        {
            var moved = annotation.Transform;
            moved.OffsetX += 1;
            copy.Annotations.Add(new RectangleAnnotation
            {
                Size = annotation.Size,
                Transform = moved,
                Style = annotation.Style,
                ZIndex = annotation.ZIndex,
            });
        }
        return copy;
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

    private static void Write(Diff diff)
    {
        var mode = BreakWysiwyg
            ? "NEGATIVE CONTROL (exported scene shifted one pixel)"
            : "POSITIVE (canvas visuals vs flattener)";

        var total = Width * Height;
        File.WriteAllText(ResultPath,
            $"mode={mode}\n" +
            $"imageSize={Width}x{Height}   ({total:N0} pixels)\n" +
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
