using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Geometry;
using Xunit;

namespace Snipwhiz.Core.Tests.Capture;

public class PixelSampleTests
{
    private static FrozenDesktop Build()
    {
        var desktop = VirtualDesktop.FromMonitors(new[]
        {
            new MonitorInfo("A", new PixelRect(-100, -50, 200, 100), 1.0, true),
        });
        var bgra = new byte[200 * 100 * 4];
        for (var y = 0; y < 100; y++)
        for (var x = 0; x < 200; x++)
        {
            var i = (y * 200 + x) * 4;
            bgra[i + 0] = 10;                  // B
            bgra[i + 1] = (byte)(y % 256);     // G
            bgra[i + 2] = (byte)(x % 256);     // R
            bgra[i + 3] = 255;
        }
        return new FrozenDesktop(desktop, bgra, CursorState.None);
    }

    [Fact]
    public void SampleAt_uses_virtual_coordinates_including_negative_ones()
    {
        var frozen = Build();
        // virtual (-100, -50) is buffer (0, 0)
        Assert.Equal((0, 0, 10), frozen.SampleAt(-100, -50));
        // virtual (-95, -45) is buffer (5, 5)
        Assert.Equal((5, 5, 10), frozen.SampleAt(-95, -45));
    }

    [Fact]
    public void SampleAt_outside_the_bounds_returns_black()
        => Assert.Equal((0, 0, 0), Build().SampleAt(10_000, 10_000));

    [Fact]
    public void SampleAt_does_not_confuse_the_red_and_green_channels()
    {
        // Sample where x != y, so R and G hold different values — the assertions
        // above use x == y, where an R/G swap is undetectable.
        var frozen = Build();
        // virtual (-93, -46) is buffer (7, 4): R = 7, G = 4, B = 10
        Assert.Equal((7, 4, 10), frozen.SampleAt(-93, -46));
    }
}
