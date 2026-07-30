using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.App.Editor;
using Snipwhiz.Core.Annotations;

namespace Snipwhiz.App.Diagnostics;

/// <summary>
/// Measures the gap between the text a <see cref="TextAnnotation"/> draws and the
/// text the editing <c>TextBox</c> shows in its place.
///
/// <para>Spec risk 3 predicted this being declared finished while still off by a
/// pixel, because a pixel of jump is invisible in a screenshot of a screenshot tool
/// and obvious the moment you click into a caption. A number is the only defence.
/// </para>
///
/// <para><b>Ink bounding box, not raw pixel equality, is the headline.</b> Two text
/// engines anti-alias differently and always will; what must not differ is where
/// the words <i>are</i>. The box offset is the thing a user perceives as a jump, and
/// it is reported to a hundredth of a pixel. The pixel diff is reported too, as a
/// secondary signal that the glyphs are the same shape and not merely in the same
/// place.</para>
///
/// <para>The box is positioned by <see cref="TextOverlayPlacement"/> — the same code
/// the editor uses. A gate that positioned its own copy would prove only that the
/// gate agrees with itself.</para>
///
/// <code>
/// $env:SNIPWHIZ_VERIFY_TEXT_SEAM = "1"
/// dotnet run --project src/Snipwhiz.App
/// # result: %TEMP%\snipwhiz-text-seam.txt
/// </code>
///
/// <para><b>NEGATIVE CONTROL</b> — moves the overlay's font size by one point, the
/// smallest change a careless edit could make. The offset must go far outside
/// tolerance:</para>
/// <code>
/// $env:SNIPWHIZ_VERIFY_BREAK_TEXT_SEAM = "1"
/// </code>
/// </summary>
internal static class TextSeamVerification
{
    private const int Width = 900;
    private const int Height = 260;

    /// <summary>
    /// Under a fifth of a pixel. Tighter than "nothing jumps" needs, so that drift
    /// is caught while it is still drift rather than once it is visible.
    /// </summary>
    private const double Tolerance = 0.2;

    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_TEXT_SEAM") == "1";

    public static bool Break =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_BREAK_TEXT_SEAM") == "1";

    private static Vector _lastInset;

    private static readonly string ResultPath =
        Path.Combine(Path.GetTempPath(), "snipwhiz-text-seam.txt");

    private readonly record struct Seam(
        double OffsetX, double OffsetY, double WidthDelta, double HeightDelta,
        int DifferingPixels, int InkPixels);

    public static void RunIfRequested()
    {
        if (!IsEnabled) return;

        // A window is needed at all only because a TextBox will not lay itself out
        // until it belongs to a presentation source; it is never shown.
        var host = new Canvas { Width = Width, Height = Height };
        var window = new Window
        {
            Title = "Snipwhiz text seam gate",
            Width = Width,
            Height = Height,
            ShowInTaskbar = false,
            WindowStyle = WindowStyle.None,
            Left = -32000,
            Content = host,
        };

        window.ContentRendered += (_, _) =>
        {
            var lines = new List<string>();

            // 96 is one image pixel per device pixel; 144 is the same scene at 150%,
            // where a non-integer scale is applied to whatever the layout produced.
            // Text that agrees at 100% and drifts at 150% is the usual shape of this
            // bug, so one DPI alone would not settle it.
            foreach (var dpi in new[] { 96.0, 144.0 })
            {
                lines.Add(Report(dpi, Compare(host, dpi)));
            }

            File.WriteAllText(ResultPath, Header() + string.Join("\n", lines));

            window.Close();
            Application.Current.Shutdown();
        };

        window.Show();
    }

    private static Seam Compare(Canvas host, double dpi)
    {
        // Deliberately awkward: mixed case for ascenders and descenders, digits,
        // punctuation that sits on the baseline, and a second line, because a
        // line-height disagreement only shows up once there are two of them.
        var annotation = new TextAnnotation
        {
            Text = "Handoff 2026-07: gjpqy — check the seam\nSecond line, for line height",
            FontSize = 26,
            Transform = new Matrix(1, 0, 0, 1, Width / 2.0, Height / 2.0),
            // No plate. Both images then contain glyphs and nothing else, so the ink
            // bounding box is the text's and not a rounded rectangle's.
            Style = TextAnnotation.DefaultStyle with { Fill = null, Stroke = Colors.White },
        };

        var drawn = RenderAnnotation(annotation, dpi);

        var box = new TextBox
        {
            AcceptsReturn = true,
            BorderThickness = new Thickness(0),
            Background = Brushes.Transparent,
            VerticalContentAlignment = VerticalAlignment.Top,
            HorizontalContentAlignment = HorizontalAlignment.Left,
            RenderTransformOrigin = new Point(0, 0),
            Text = annotation.Text,
        };

        host.Children.Clear();
        host.Children.Add(box);

        // Positioned by the editor's own code, inset measurement included.
        var inset = TextOverlayPlacement.Apply(box, annotation, 1, default);

        // The control: one point larger, which is the smallest slip a careless edit
        // to the overlay could make and exactly what this must not miss. Applied
        // after placement so the box is positioned as if nothing were wrong, which
        // is how a real mismatch would present.
        if (Break)
        {
            box.FontSize += 1;
            box.UpdateLayout();
        }

        host.UpdateLayout();
        _lastInset = inset;

        var typed = Render(host, dpi);

        var a = Ink(drawn);
        var b = Ink(typed);
        var scale = dpi / 96.0;

        return new Seam(
            // Back into image pixels, so the two DPI runs report on the same scale
            // and "0.3px at 150%" does not read as worse than it is.
            (b.X - a.X) / scale,
            (b.Y - a.Y) / scale,
            (b.Width - a.Width) / scale,
            (b.Height - a.Height) / scale,
            DifferingPixels(drawn, typed),
            InkCount(drawn));
    }

    /// <summary>
    /// Text draws glyphs, not pixels, so the capture it is handed is never read.
    /// A one-pixel stand-in says that out loud; passing a real capture here would
    /// suggest this gate depended on it.
    /// </summary>
    private static readonly BitmapSource NoCapture =
        BitmapSource.Create(1, 1, 96, 96, PixelFormats.Bgra32, null, new byte[4], 4);

    private static BitmapSource RenderAnnotation(TextAnnotation annotation, double dpi)
    {
        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen()) annotation.Render(dc, NoCapture);
        return Render(visual, dpi);
    }

    private static BitmapSource Render(Visual visual, double dpi)
    {
        var scale = dpi / 96.0;
        var target = new RenderTargetBitmap(
            (int)(Width * scale), (int)(Height * scale), dpi, dpi, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>The tightest box containing anything drawn, in device pixels.</summary>
    private static Rect Ink(BitmapSource image)
    {
        var pixels = Pixels(image, out var stride);

        int minX = int.MaxValue, minY = int.MaxValue, maxX = -1, maxY = -1;
        for (var y = 0; y < image.PixelHeight; y++)
        {
            for (var x = 0; x < image.PixelWidth; x++)
            {
                // Alpha, not colour: everything drawn here is white on nothing, and
                // a threshold on alpha ignores how each engine chose to anti-alias.
                // Well above zero so a stray one-off value does not set the edge.
                if (pixels[y * stride + x * 4 + 3] < 40) continue;
                if (x < minX) minX = x;
                if (y < minY) minY = y;
                if (x > maxX) maxX = x;
                if (y > maxY) maxY = y;
            }
        }

        return maxX < 0 ? Rect.Empty : new Rect(minX, minY, maxX - minX + 1, maxY - minY + 1);
    }

    private static int InkCount(BitmapSource image)
    {
        var pixels = Pixels(image, out var stride);
        var count = 0;
        for (var i = 3; i < pixels.Length; i += 4) if (pixels[i] >= 40) count++;
        return count;
    }

    private static int DifferingPixels(BitmapSource a, BitmapSource b)
    {
        var left = Pixels(a, out var stride);
        var right = Pixels(b, out _);

        var differing = 0;
        for (var i = 0; i < left.Length; i += 4)
        {
            // Generous: anti-aliasing between two text engines differs on every edge
            // pixel, and counting those would drown the signal.
            if (Math.Abs(left[i + 3] - right[i + 3]) > 64) differing++;
        }
        return differing;
    }

    private static byte[] Pixels(BitmapSource image, out int stride)
    {
        stride = image.PixelWidth * 4;
        var pixels = new byte[stride * image.PixelHeight];
        image.CopyPixels(pixels, stride, 0);
        return pixels;
    }

    private static string Header() =>
        $"mode={(Break ? "NEGATIVE CONTROL (overlay font one point larger)" : "POSITIVE (annotation vs editing TextBox)")}\n" +
        $"tolerance={Tolerance} image pixels\n\n";

    /// <summary>
    /// Share of the inked pixels allowed to differ. Two text engines disagree on
    /// edge anti-aliasing and always will; a wrong font disagrees on everything.
    /// </summary>
    private const double MaxDifferingShare = 0.02;

    private static string Report(double dpi, Seam seam)
    {
        var offset = Math.Max(Math.Abs(seam.OffsetX), Math.Abs(seam.OffsetY));
        var share = seam.InkPixels == 0 ? 1 : (double)seam.DifferingPixels / seam.InkPixels;

        // Both, and that is not caution — it is what the control demanded. One point
        // larger produced an ink box whose top-left landed in exactly the same place
        // at 150%: offset 0.000, "within tolerance", for text plainly the wrong size.
        // The pixel share caught it by a factor of a thousand. Offset alone would
        // have passed the very mistake this gate exists to catch.
        var verdict = offset <= Tolerance && share <= MaxDifferingShare
            ? "WITHIN TOLERANCE"
            : "OUT OF TOLERANCE";

        return $"--- {dpi / 96.0 * 100:F0}% DPI ---\n" +
               $"inkOffsetX={seam.OffsetX:F3}\n" +
               $"inkOffsetY={seam.OffsetY:F3}\n" +
               $"inkWidthDelta={seam.WidthDelta:F3}\n" +
               $"inkHeightDelta={seam.HeightDelta:F3}\n" +
               $"differingPixels={seam.DifferingPixels}  (of {seam.InkPixels} inked, {share:P2})\n" +
               $"measuredBoxInset={_lastInset.X:F2},{_lastInset.Y:F2}\n" +
               $"verdict={verdict}\n";
    }
}
