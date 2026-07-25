using Snipwhiz.Core.Geometry;
using Xunit;

namespace Snipwhiz.Core.Tests.Geometry;

public class VirtualDesktopTests
{
    // Primary 1920x1080 at 100%, secondary 2560x1440 at 150% placed LEFT and ABOVE.
    // This is the layout that breaks naive implementations: negative origin.
    private static VirtualDesktop MixedDpiNegativeOrigin() => VirtualDesktop.FromMonitors(new[]
    {
        new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 1920, 1080), 1.0, true),
        new MonitorInfo(@"\\.\DISPLAY2", new PixelRect(-2560, -360, 2560, 1440), 1.5, false),
    });

    [Fact]
    public void Bounds_is_the_union_and_may_have_a_negative_origin()
    {
        var d = MixedDpiNegativeOrigin();
        Assert.Equal(new PixelRect(-2560, -360, 4480, 1440), d.Bounds);
    }

    [Fact]
    public void Single_monitor_bounds_equal_that_monitor()
    {
        var d = VirtualDesktop.FromMonitors(new[]
        {
            new MonitorInfo(@"\\.\DISPLAY1", new PixelRect(0, 0, 2560, 1440), 1.0, true),
        });
        Assert.Equal(new PixelRect(0, 0, 2560, 1440), d.Bounds);
    }

    [Fact]
    public void MonitorAt_finds_the_monitor_under_a_negative_coordinate()
    {
        var d = MixedDpiNegativeOrigin();
        Assert.Equal(@"\\.\DISPLAY2", d.MonitorAt(-1000, 0)!.Value.DeviceName);
        Assert.Equal(1.5, d.MonitorAt(-1000, 0)!.Value.Scale);
    }

    [Fact]
    public void MonitorAt_returns_null_in_an_uncovered_gap()
    {
        var d = MixedDpiNegativeOrigin();
        // top-right of the bounding box: inside Bounds, covered by no display
        Assert.Null(d.MonitorAt(1000, -200));
        Assert.False(d.IsCovered(1000, -200));
    }

    [Fact]
    public void IsCovered_is_true_inside_a_monitor()
    {
        var d = MixedDpiNegativeOrigin();
        Assert.True(d.IsCovered(10, 10));
        Assert.True(d.IsCovered(-2560, -360));       // inclusive top-left
        Assert.False(d.IsCovered(1920, 0));          // exclusive right edge
    }

    [Fact]
    public void Three_monitors_at_three_scales_all_resolve()
    {
        var d = VirtualDesktop.FromMonitors(new[]
        {
            new MonitorInfo("A", new PixelRect(0, 0, 1920, 1080), 1.0, true),
            new MonitorInfo("B", new PixelRect(1920, 0, 2560, 1440), 1.5, false),
            new MonitorInfo("C", new PixelRect(4480, 0, 3840, 2160), 2.25, false),
        });
        Assert.Equal(1.0,  d.MonitorAt(100, 100)!.Value.Scale);
        Assert.Equal(1.5,  d.MonitorAt(2000, 100)!.Value.Scale);
        Assert.Equal(2.25, d.MonitorAt(5000, 100)!.Value.Scale);
        Assert.Equal(new PixelRect(0, 0, 8320, 2160), d.Bounds);
    }
}
