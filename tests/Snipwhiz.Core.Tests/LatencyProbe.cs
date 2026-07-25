using System.Diagnostics;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Monitors;
using Xunit;
using Xunit.Abstractions;

namespace Snipwhiz.Core.Tests;

/// <summary>
/// The spec's section 4.5 gate. If the median exceeds 120 ms on the worst
/// monitor configuration we ship to, STOP and switch the freeze path to DXGI
/// Desktop Duplication before building the overlay.
/// </summary>
public class LatencyProbe(ITestOutputHelper output)
{
    [Fact]
    public void Grab_completes_within_the_paint_budget()
    {
        var grabber = new BitBltGrabber();
        grabber.Grab();                       // discard the first, it pays JIT and handle setup

        var samples = new List<double>();
        for (var i = 0; i < 15; i++)
        {
            var sw = Stopwatch.StartNew();
            var frozen = grabber.Grab();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
            Assert.Equal(frozen.Width * frozen.Height * 4L, frozen.Bgra.LongLength);
        }

        samples.Sort();
        var median = samples[samples.Count / 2];
        var worst = samples[^1];

        var desktop = Snipwhiz.Core.Geometry.VirtualDesktop.FromMonitors(MonitorEnumerator.Enumerate());
        var megapixels = desktop.Bounds.Width * (double)desktop.Bounds.Height / 1_000_000;

        output.WriteLine($"displays   : {desktop.Monitors.Count}");
        output.WriteLine($"virtual    : {desktop.Bounds.Width}x{desktop.Bounds.Height} ({megapixels:F1} MP)");
        output.WriteLine($"scales     : {string.Join(", ", desktop.Monitors.Select(m => $"{m.Scale:P0}"))}");
        output.WriteLine($"median     : {median:F1} ms");
        output.WriteLine($"worst      : {worst:F1} ms");

        Assert.True(median < 120,
            $"Grab median {median:F1} ms exceeds the 120 ms budget. Per spec section 4.5, " +
            "switch the freeze path to DXGI Desktop Duplication before continuing.");
    }
}
