using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Geometry;
using Xunit;

namespace Snipwhiz.Core.Tests.Capture;

public class FrozenDesktopCropTests
{
    /// <summary>Builds a frozen desktop whose blue channel encodes X and green encodes Y.</summary>
    private static FrozenDesktop Build(VirtualDesktop desktop)
    {
        var b = desktop.Bounds;
        var bgra = new byte[(long)b.Width * b.Height * 4];
        for (var y = 0; y < b.Height; y++)
        for (var x = 0; x < b.Width; x++)
        {
            var i = ((long)y * b.Width + x) * 4;
            bgra[i + 0] = (byte)(x % 256);
            bgra[i + 1] = (byte)(y % 256);
            bgra[i + 2] = 0;
            bgra[i + 3] = 255;
        }
        return new FrozenDesktop(desktop, bgra, CursorState.None);
    }

    private static VirtualDesktop TwoMonitorsNegativeOrigin() => VirtualDesktop.FromMonitors(new[]
    {
        new MonitorInfo("A", new PixelRect(0, 0, 1920, 1080), 1.0, true),
        new MonitorInfo("B", new PixelRect(-2560, 0, 2560, 1080), 1.5, false),
    });

    [Fact]
    public void Crop_translates_by_the_virtual_origin_not_by_monitor()
    {
        var frozen = Build(TwoMonitorsNegativeOrigin());
        // A 4x2 region starting at virtual (-2560, 0) is buffer offset (0, 0).
        var crop = frozen.Crop(new PixelRect(-2560, 0, 4, 2));

        Assert.Equal(4, crop.Width);
        Assert.Equal(2, crop.Height);
        Assert.Equal(0, crop.Bgra[0]);                 // x = 0
        Assert.Equal(3, crop.Bgra[3 * 4 + 0]);         // x = 3
        Assert.Equal(1, crop.Bgra[(4 + 0) * 4 + 1]);   // second row, y = 1
    }

    [Fact]
    public void Crop_spanning_two_monitors_is_one_contiguous_image()
    {
        var frozen = Build(TwoMonitorsNegativeOrigin());
        // straddles the seam at virtual x = 0
        var crop = frozen.Crop(new PixelRect(-10, 10, 20, 5));

        Assert.Equal(20, crop.Width);
        Assert.Equal(5, crop.Height);
        Assert.False(crop.HasUncoveredPixels);
        // buffer x for virtual -10 is 2550
        Assert.Equal((byte)(2550 % 256), crop.Bgra[0]);
    }

    [Fact]
    public void Crop_reports_uncovered_pixels_in_an_L_shaped_desktop()
    {
        // Second display is shorter and offset up, leaving a gap bottom-right.
        var desktop = VirtualDesktop.FromMonitors(new[]
        {
            new MonitorInfo("A", new PixelRect(0, 0, 100, 100), 1.0, true),
            new MonitorInfo("B", new PixelRect(100, 0, 100, 50), 1.0, false),
        });
        var frozen = Build(desktop);

        Assert.True(frozen.Crop(new PixelRect(120, 60, 40, 30)).HasUncoveredPixels);
        Assert.False(frozen.Crop(new PixelRect(10, 10, 40, 30)).HasUncoveredPixels);
        Assert.False(frozen.Crop(new PixelRect(120, 10, 40, 30)).HasUncoveredPixels);
    }

    [Fact]
    public void Crop_clamps_a_region_that_runs_past_the_bounds()
    {
        var frozen = Build(TwoMonitorsNegativeOrigin());
        var crop = frozen.Crop(new PixelRect(1900, 1060, 500, 500));
        Assert.Equal(20, crop.Width);
        Assert.Equal(20, crop.Height);
    }

    [Fact]
    public void Crop_of_an_empty_region_throws()
    {
        var frozen = Build(TwoMonitorsNegativeOrigin());
        Assert.Throws<ArgumentException>(() => frozen.Crop(new PixelRect(10, 10, 0, 0)));
    }

    [Fact]
    public void Crop_of_a_single_pixel_works()
    {
        var frozen = Build(TwoMonitorsNegativeOrigin());
        var crop = frozen.Crop(new PixelRect(-2555, 7, 1, 1));
        Assert.Equal(1, crop.Width);
        Assert.Equal(5, crop.Bgra[0]);
        Assert.Equal(7, crop.Bgra[1]);
    }
}
