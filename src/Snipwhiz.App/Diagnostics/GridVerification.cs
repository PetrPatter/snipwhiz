using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Snipwhiz.App.Library;

namespace Snipwhiz.App.Diagnostics;

/// <summary>
/// Measures whether the library grid is actually virtualizing.
///
/// "It scrolled smoothly" is not evidence — a dev machine hides jank that a
/// family laptop will not, and an <see cref="ItemsControl"/> silently realizes
/// every row if either the virtualizing panel or the item-scrolling ScrollViewer
/// is missing. This drives the scroll viewer from top to bottom and counts
/// realized <see cref="CaptureTile"/> instances at each step: virtualizing, the
/// peak stays a small multiple of one screenful; not virtualizing, it climbs to
/// the size of the library.
///
/// The sweep is driven here rather than by hand so the number is reproducible and
/// does not depend on how far someone happened to scroll.
///
/// <para><b>POSITIVE</b> — peak must stay bounded:</para>
/// <code>
/// $env:SNIPWHIZ_ROOT = "$env:TEMP\snipwhiz-verify"
/// $env:SNIPWHIZ_SEED = "1000"
/// $env:SNIPWHIZ_VERIFY_GRID = "1"
/// dotnet run --project src/Snipwhiz.App
/// # result: %TEMP%\snipwhiz-grid-verify.txt
/// </code>
///
/// <para><b>NEGATIVE CONTROL</b> — swaps in a plain <c>StackPanel</c>; the peak
/// must climb to the row count. A check that has never been seen failing is not
/// known to work:</para>
/// <code>
/// $env:SNIPWHIZ_VERIFY_BREAK_VIRTUALIZATION = "1"
/// </code>
/// </summary>
internal static class GridVerification
{
    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_GRID") == "1";

    public static bool BreakVirtualization =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_BREAK_VIRTUALIZATION") == "1";

    private static readonly string ResultPath =
        Path.Combine(Path.GetTempPath(), "snipwhiz-grid-verify.txt");

    /// <summary>
    /// Scrolls to the bottom in viewport-sized steps, sampling as it goes, then
    /// writes the report.
    /// </summary>
    public static void Sweep(ItemsControl host, ScrollViewer scroller, Func<int> loadedCaptures)
    {
        if (!IsEnabled) return;

        var peak = 0;
        var samples = 0;
        var stalls = 0;
        var lastOffset = -1.0;

        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            // Long enough for the panel to realize containers and for a page fetch
            // to land before the next step.
            Interval = TimeSpan.FromMilliseconds(180),
        };

        timer.Tick += (_, _) =>
        {
            var realized = CountTiles(host);
            samples++;
            if (realized > peak) peak = realized;

            var atBottom = scroller.VerticalOffset >= scroller.ScrollableHeight - 1;
            // Paging appends rows as we go, so "no movement" rather than "reached
            // the end" is what says the sweep is finished.
            if (Math.Abs(scroller.VerticalOffset - lastOffset) < 0.5) stalls++;
            else stalls = 0;
            lastOffset = scroller.VerticalOffset;

            if ((atBottom && stalls >= 3) || samples > 400)
            {
                timer.Stop();
                Write(peak, samples, loadedCaptures(), scroller);
                return;
            }

            scroller.ScrollToVerticalOffset(
                scroller.VerticalOffset + Math.Max(1, scroller.ViewportHeight));
        };

        timer.Start();
    }

    private static void Write(int peak, int samples, int loaded, ScrollViewer scroller)
    {
        var mode = BreakVirtualization
            ? "NEGATIVE CONTROL (plain StackPanel — virtualization deliberately off)"
            : "POSITIVE (VirtualizingStackPanel, recycling)";

        File.WriteAllText(ResultPath,
            $"mode={mode}\n" +
            $"capturesLoaded={loaded}\n" +
            $"realizedTilesPeak={peak}\n" +
            $"samples={samples}\n" +
            $"viewportHeight={scroller.ViewportHeight:F0}\n" +
            $"extentHeight={scroller.ExtentHeight:F0}\n" +
            "\n" +
            "Virtualizing: peak is a small multiple of one screenful and does not\n" +
            "track capturesLoaded. Not virtualizing: peak approaches capturesLoaded.\n");
    }

    private static int CountTiles(DependencyObject root)
    {
        var count = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is CaptureTile) count++;

            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }

        return count;
    }
}
