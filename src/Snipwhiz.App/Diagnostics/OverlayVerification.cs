using System.IO;
using System.Windows.Threading;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Geometry;

namespace Snipwhiz.App.Diagnostics;

/// <summary>
/// Manual, re-auditable verification of the Task 8 1:1-physical-pixel overlay
/// rendering rule.
///
/// The original verification method (grab the whole screen again, diff it against
/// the whole frozen buffer) could not discriminate a correct render from a
/// mis-scaled one: a real whole-monitor 1.25x scale bug registered only 0.6%
/// mismatch against a photographic desktop, because most of a desktop's pixels
/// are locally uniform and survive a wrong scale factor well enough to look
/// "mostly right" byte-for-byte. This stamps a 1-pixel checkerboard marker block
/// into the frozen buffer at known physical coordinates on every monitor before
/// showing the overlay: any resampling, any scale error, any positional offset
/// destroys the exact alternating pattern, so a correct render must reproduce it
/// byte-for-byte and an incorrect one reliably will not.
///
/// Completely inert unless explicitly enabled — one environment-variable read, no
/// behavior change, does not touch the app's real <see cref="CaptureSession"/>
/// field — so it costs nothing in normal operation but stays in the tree,
/// runnable, for the next time this needs re-auditing.
///
/// <para><b>POSITIVE CONTROL</b> — must report <c>mismatched=0</c> on every
/// monitor:</para>
/// <code>
/// $env:SNIPWHIZ_VERIFY_OVERLAY = "1"
/// dotnet run --project src/Snipwhiz.App
/// # press Ctrl+Shift+1; result written to %TEMP%\snipwhiz-overlay-verify.txt
/// </code>
///
/// <para><b>NEGATIVE CONTROL</b> — must report <c>mismatched &gt; 0</c> on the
/// first monitor. A check that has never been observed to fail is not known to
/// work; this feeds <see cref="OverlayWindow"/> a deliberately wrong
/// <see cref="MonitorInfo.Scale"/> for the first monitor, reproducing the exact
/// class of bug (incorrect DIP sizing) this task fixed:</para>
/// <code>
/// $env:SNIPWHIZ_VERIFY_OVERLAY = "1"
/// $env:SNIPWHIZ_VERIFY_BREAK_SCALE = "1"
/// dotnet run --project src/Snipwhiz.App
/// # press Ctrl+Shift+1; result written to the same file
/// </code>
/// </summary>
internal static class OverlayVerification
{
    public static bool IsEnabled =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_OVERLAY") == "1";

    private static readonly string ResultPath =
        Path.Combine(Path.GetTempPath(), "snipwhiz-overlay-verify.txt");

    private const int MarkerSize = 40;   // physical pixels, square
    private const int MarkerOffset = 40; // from each monitor's top-left corner

    public static void Run(FrozenDesktop frozen)
    {
        var breakScale = Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_BREAK_SCALE") == "1";

        var stampedBgra = (byte[])frozen.Bgra.Clone();
        var markers = new List<(string Device, PixelRect Rect)>();
        foreach (var m in frozen.Desktop.Monitors)
        {
            var rect = new PixelRect(m.Bounds.X + MarkerOffset, m.Bounds.Y + MarkerOffset, MarkerSize, MarkerSize);
            StampCheckerboard(stampedBgra, frozen.Bounds, rect);
            markers.Add((m.DeviceName, rect));
        }

        // Negative control: feed the overlay a wrong Scale for the first monitor —
        // the window itself is still positioned correctly (SetWindowPos uses
        // Bounds, untouched), but RenderFrozenSlice sizes the Image in DIPs using
        // this wrong value, reproducing the exact incorrect-DIP-size bug class.
        var monitorList = frozen.Desktop.Monitors.ToArray();
        var testMonitors = breakScale
            ? monitorList.Select((m, i) => i == 0 ? m with { Scale = 1.0 } : m).ToArray()
            : monitorList;
        var testDesktop = VirtualDesktop.FromMonitors(testMonitors);
        var testFrozen = new FrozenDesktop(testDesktop, stampedBgra, frozen.Cursor);

        var session = new CaptureSession(testFrozen);
        if (!session.Start())
        {
            File.WriteAllText(ResultPath,
                "FAILED: verification session could not take focus (SetForegroundWindow refused)\n");
            session.Dispose();
            return;
        }

        var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(800) };
        settle.Tick += (_, _) =>
        {
            settle.Stop();
            try
            {
                var regrab = new BitBltGrabber().Grab();
                WriteReport(testFrozen, regrab, markers, breakScale);
            }
            finally
            {
                session.Cancel();
            }
        };
        settle.Start();
    }

    private static void StampCheckerboard(byte[] bgra, PixelRect bounds, PixelRect block)
    {
        for (var y = 0; y < block.Height; y++)
        for (var x = 0; x < block.Width; x++)
        {
            var gx = block.X + x - bounds.X;
            var gy = block.Y + y - bounds.Y;
            var offset = ((long)gy * bounds.Width + gx) * 4;
            byte v = (byte)((x + y) % 2 == 0 ? 255 : 0);
            bgra[offset] = v;
            bgra[offset + 1] = v;
            bgra[offset + 2] = v;
            bgra[offset + 3] = 255;
        }
    }

    private static void WriteReport(
        FrozenDesktop expected, FrozenDesktop actual,
        List<(string Device, PixelRect Rect)> markers, bool breakScale)
    {
        var lines = new List<string>
        {
            $"mode={(breakScale ? "NEGATIVE CONTROL (Scale forced wrong on first monitor)" : "POSITIVE CONTROL")}"
        };

        foreach (var (device, rect) in markers)
        {
            long mismatched = 0;
            var total = (long)rect.Width * rect.Height;
            for (var y = 0; y < rect.Height; y++)
            for (var x = 0; x < rect.Width; x++)
            {
                var ex = rect.X + x - expected.Bounds.X;
                var ey = rect.Y + y - expected.Bounds.Y;
                var eo = ((long)ey * expected.Bounds.Width + ex) * 4;

                var ax = rect.X + x - actual.Bounds.X;
                var ay = rect.Y + y - actual.Bounds.Y;
                var ao = ((long)ay * actual.Bounds.Width + ax) * 4;

                if (expected.Bgra[eo] != actual.Bgra[ao] ||
                    expected.Bgra[eo + 1] != actual.Bgra[ao + 1] ||
                    expected.Bgra[eo + 2] != actual.Bgra[ao + 2])
                    mismatched++;
            }
            lines.Add($"{device} marker@({rect.X},{rect.Y}) {rect.Width}x{rect.Height}: " +
                      $"mismatched={mismatched}/{total} ({100.0 * mismatched / total:F2}%)");
        }

        File.WriteAllLines(ResultPath, lines);
    }
}
