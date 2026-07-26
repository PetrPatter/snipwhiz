using System.IO;
using System.Windows;
using System.Windows.Media;
using Snipwhiz.App.Library;

namespace Snipwhiz.App.Diagnostics;

/// <summary>
/// Measures whether the library grid is actually virtualizing.
///
/// "It scrolled smoothly" is not evidence — a dev machine hides jank that a
/// family laptop will not, and an <see cref="System.Windows.Controls.ItemsControl"/>
/// silently realizes every row if either the virtualizing panel or the
/// item-scrolling ScrollViewer is missing. This counts realized
/// <see cref="CaptureTile"/> instances in the visual tree: with virtualization
/// the peak stays a small multiple of what fits on screen, and without it the
/// peak climbs to the size of the library.
///
/// Inert unless enabled — one environment-variable read.
///
/// <para><b>POSITIVE</b> — scroll a large library top to bottom; peak must stay
/// bounded:</para>
/// <code>
/// $env:SNIPWHIZ_VERIFY_GRID = "1"
/// dotnet run --project src/Snipwhiz.App
/// # open the library, scroll to the bottom, close
/// # result: %TEMP%\snipwhiz-grid-verify.txt
/// </code>
///
/// <para><b>NEGATIVE CONTROL</b> — replace the ItemsPanel with a plain
/// <c>StackPanel</c> in LibraryWindow.xaml and repeat. The peak must climb to
/// the row count. A check that has never been seen failing is not known to
/// work.</para>
/// </summary>
internal static class GridVerification
{
    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_GRID") == "1";

    private static readonly string ResultPath =
        Path.Combine(Path.GetTempPath(), "snipwhiz-grid-verify.txt");

    private static int _peak;
    private static int _samples;

    public static void Sample(DependencyObject root, int loadedCaptures)
    {
        if (!IsEnabled) return;

        var realized = CountTiles(root);
        _samples++;
        if (realized > _peak) _peak = realized;

        File.WriteAllText(ResultPath,
            $"samples={_samples}\n" +
            $"capturesLoaded={loadedCaptures}\n" +
            $"realizedTilesNow={realized}\n" +
            $"realizedTilesPeak={_peak}\n" +
            "\n" +
            "Virtualizing: peak stays a small multiple of one screenful and does\n" +
            "not track capturesLoaded. Not virtualizing: peak approaches\n" +
            "capturesLoaded. Compare against the StackPanel negative control.\n");
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
