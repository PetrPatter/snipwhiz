using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.App.Editor;
using Snipwhiz.Core.Annotations;
using Snipwhiz.Core.Scene;

namespace Snipwhiz.App.Diagnostics;

/// <summary>
/// Measures whether the canvas actually invalidates per object.
///
/// <para>"It felt smooth" is not evidence. On a fast machine a full rebuild is
/// indistinguishable from a single-object re-render until the scene is large, and
/// by then the cost is spread across every interaction rather than being visible
/// in one. So this counts <see cref="CanvasHost.VisualRenderCount"/> across three
/// operations on a 200-object scene, where the expected numbers are far apart:</para>
///
/// <list type="bullet">
/// <item><b>zooming</b> must render nothing — the view transform is on the
/// container, so no object is redrawn at all;</item>
/// <item><b>moving one object</b> must render exactly one;</item>
/// <item><b>a structural change</b> rebuilds, and is the only thing that should.</item>
/// </list>
///
/// <code>
/// $env:SNIPWHIZ_VERIFY_CANVAS = "1"
/// dotnet run --project src/Snipwhiz.App
/// # result: %TEMP%\snipwhiz-canvas-verify.txt
/// </code>
///
/// <para><b>NEGATIVE CONTROL</b> — makes every change a full rebuild, which is what
/// an element-per-object or single-OnRender canvas would do:</para>
/// <code>
/// $env:SNIPWHIZ_VERIFY_BREAK_INVALIDATION = "1"
/// </code>
/// </summary>
internal static class CanvasVerification
{
    private const int Objects = 200;

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_CANVAS") == "1";

    public static bool BreakInvalidation =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_BREAK_INVALIDATION") == "1";

    private static readonly string ResultPath =
        Path.Combine(Path.GetTempPath(), "snipwhiz-canvas-verify.txt");

    public static void RunIfRequested()
    {
        if (!IsEnabled) return;

        var canvas = new CanvasHost();
        var window = new Window
        {
            Title = "Snipwhiz canvas gate",
            Width = 900,
            Height = 640,
            Background = Brushes.Black,
            Content = canvas,
        };

        window.ContentRendered += (_, _) =>
        {
            var document = BuildScene();
            canvas.Load(Source(1200, 800), document);
            canvas.Fit();

            var afterLoad = canvas.VisualRenderCount;

            // 1. Zoom. The transform is on the container, so nothing should redraw.
            var zoomWatch = Stopwatch.StartNew();
            for (var i = 0; i < 20; i++)
                canvas.ZoomAt(canvas.Zoom * 1.05, new Point(450, 320));
            zoomWatch.Stop();
            var afterZoom = canvas.VisualRenderCount;

            // 2. Move one object forty times, as a drag would.
            var moved = document.Annotations[Objects / 2];
            var moveWatch = Stopwatch.StartNew();
            for (var i = 1; i <= 40; i++)
            {
                moved.Transform = new Matrix(1, 0, 0, 1, 100 + i, 100 + i);
                Invalidate(canvas, moved);
            }
            moveWatch.Stop();
            var afterMove = canvas.VisualRenderCount;

            // 3. A structural change, which is the only thing that may rebuild.
            document.Annotations.Add(Rectangle(Objects, Objects));
            canvas.Rebuild();
            var afterRebuild = canvas.VisualRenderCount;

            Write(afterLoad, afterZoom, afterMove, afterRebuild,
                  zoomWatch.Elapsed.TotalMilliseconds, moveWatch.Elapsed.TotalMilliseconds);

            window.Close();
            // The tray keeps the app alive, so closing the gate's own window is not
            // enough to end the run.
            Application.Current.Shutdown();
        };

        window.Show();
    }

    /// <summary>
    /// The control: rebuild everything instead of re-rendering one object, which is
    /// what a canvas without retained per-object visuals is forced to do.
    /// </summary>
    private static void Invalidate(CanvasHost canvas, Annotation annotation)
    {
        if (BreakInvalidation) canvas.Rebuild();
        else canvas.Invalidate(annotation);
    }

    private static SceneDocument BuildScene()
    {
        var document = new SceneDocument { CaptureId = Guid.CreateVersion7() };
        for (var i = 0; i < Objects; i++) document.Annotations.Add(Rectangle(i, i));
        return document;
    }

    private static RectangleAnnotation Rectangle(int index, int z) => new()
    {
        ZIndex = z,
        Size = new Size(60, 40),
        Transform = new Matrix(1, 0, 0, 1, 40 + index % 20 * 55, 40 + index / 20 * 70),
        Style = AnnotationStyle.Default with
        {
            Stroke = Color.FromRgb((byte)(40 + index * 7 % 200), 0x84, (byte)(40 + index * 13 % 200)),
        },
    };

    private static BitmapSource Source(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        Array.Fill(pixels, (byte)32);
        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }

    private static void Write(int load, int zoom, int move, int rebuild, double zoomMs, double moveMs)
    {
        var mode = BreakInvalidation
            ? "NEGATIVE CONTROL (every change rebuilds the scene)"
            : "POSITIVE (retained per-object visuals)";

        File.WriteAllText(ResultPath,
            $"mode={mode}\n" +
            $"objects={Objects}\n" +
            "\n" +
            $"rendersAfterLoad={load}\n" +
            $"rendersAfterZoomSweep={zoom}   (delta {zoom - load}, {zoomMs:F1} ms for 20 steps)\n" +
            $"rendersAfterMovingOneObject40x={move}   (delta {move - zoom}, {moveMs:F1} ms)\n" +
            $"rendersAfterStructuralRebuild={rebuild}   (delta {rebuild - move})\n" +
            "\n" +
            "Expected, per-object invalidation:\n" +
            $"  load       = {Objects}\n" +
            "  zoom delta = 0    the view transform is on the container\n" +
            "  move delta = 40   one object, once per step\n" +
            $"  rebuild    = {Objects + 1}\n" +
            "\n" +
            "Not per-object: the move delta approaches 40 x objects, because every\n" +
            "step redraws the whole scene.\n");
    }
}
