using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Snipwhiz.App.Library;

namespace Snipwhiz.App.Diagnostics;

/// <summary>
/// Checks that resizing the window does not blank the thumbnails that stay on
/// screen through it.
///
/// This exists because the grid sweep cannot see the bug. <see cref="GridVerification"/>
/// scrolls in one direction and never resizes, so it measures whether retention is
/// bounded and says nothing about whether anything is still displayed. Two separate
/// release bugs shipped past it and were caught by eye instead: releasing on an
/// element's own liveness dropped the bitmap a replacement tile had just decoded,
/// because one view model is shared by every tile showing that capture.
///
/// Changing the window width changes the column count, which rebuilds every row —
/// so every tile currently on screen is discarded and rebuilt while remaining
/// visible. That is the exact condition both bugs needed.
///
/// <para><b>POSITIVE</b> — <c>blankTiles</c> must be 0:</para>
/// <code>
/// $env:SNIPWHIZ_ROOT = "$env:TEMP\snipwhiz-verify"
/// $env:SNIPWHIZ_SEED = "200"
/// $env:SNIPWHIZ_VERIFY_RESIZE = "1"
/// dotnet run --project src/Snipwhiz.App
/// # result: %TEMP%\snipwhiz-resize-verify.txt
/// </code>
///
/// <para><b>NEGATIVE CONTROL</b> — releases the moment a tile detaches, which is
/// the behaviour that produced the black tiles. <c>blankTiles</c> must be non-zero:</para>
/// <code>
/// $env:SNIPWHIZ_VERIFY_BREAK_RELEASE = "1"
/// </code>
/// </summary>
internal static class ResizeVerification
{
    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_RESIZE") == "1";

    /// <summary>Read by <see cref="CaptureTile"/>; skips the deferred release.</summary>
    public static bool BreakRelease =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_BREAK_RELEASE") == "1";

    private static readonly string ResultPath =
        Path.Combine(Path.GetTempPath(), "snipwhiz-resize-verify.txt");

    /// <summary>
    /// Narrows the window by two tiles, widens it back, then counts realized tiles
    /// that are showing nothing. Steps are spaced well beyond a cached-JPEG decode
    /// so a blank at the end means "never came back", not "still working".
    /// </summary>
    public static void Run(Window window, ItemsControl host)
    {
        if (!IsEnabled) return;

        // ActualWidth, not Width: Width is NaN unless XAML set it, and NaN
        // arithmetic would silently turn every resize step into a no-op.
        var original = window.ActualWidth;
        var step = 0;
        var worst = 0;
        var sampled = 0;

        var timer = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(600),
        };

        timer.Tick += (_, _) =>
        {
            switch (step++)
            {
                case 0: window.Width = original - 2 * 268; break;
                case 1: window.Width = original; break;
                case 2: window.Width = original - 268; break;
                case 3: window.Width = original; break;
                // Everything after this is settle time; sample rather than resize.
                default:
                    var (blank, total) = Count(host);
                    sampled = total;
                    if (blank > worst) worst = blank;
                    if (step > 8)
                    {
                        timer.Stop();
                        Write(worst, sampled);
                    }
                    break;
            }
        };

        timer.Start();
    }

    private static void Write(int blank, int realized) =>
        File.WriteAllText(ResultPath,
            $"mode={(BreakRelease ? "NEGATIVE CONTROL (release on detach, not deferred)" : "POSITIVE")}\n" +
            $"realizedTiles={realized}\n" +
            $"blankTiles={blank}\n" +
            "\n" +
            "blankTiles counts realized tiles whose capture decoded nothing and is\n" +
            "not flagged missing. Correct release: 0. A release that drops a bitmap\n" +
            "another tile is still showing: non-zero, and those tiles stay black\n" +
            "until scrolling regenerates their containers.\n");

    private static (int Blank, int Realized) Count(DependencyObject root)
    {
        var blank = 0;
        var realized = 0;
        var stack = new Stack<DependencyObject>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is CaptureTile { DataContext: CaptureTileViewModel model })
            {
                realized++;
                if (model.Thumbnail is null && !model.IsMissing) blank++;
            }

            var children = VisualTreeHelper.GetChildrenCount(node);
            for (var i = 0; i < children; i++)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }

        return (blank, realized);
    }
}
