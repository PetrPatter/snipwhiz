using Snipwhiz.Core.Geometry;
using Xunit;

namespace Snipwhiz.Core.Tests.Geometry;

public class PixelRectTests
{
    [Theory]
    // drag right-down, left-up, right-up, left-down — all yield the same rect
    [InlineData(10, 20, 110, 220)]
    [InlineData(110, 220, 10, 20)]
    [InlineData(110, 20, 10, 220)]
    [InlineData(10, 220, 110, 20)]
    public void FromCorners_normalizes_every_drag_direction(int x1, int y1, int x2, int y2)
    {
        var r = PixelRect.FromCorners(x1, y1, x2, y2);
        Assert.Equal(new PixelRect(10, 20, 100, 200), r);
    }

    [Fact]
    public void FromCorners_handles_negative_virtual_origin()
    {
        // a monitor left of and above primary
        var r = PixelRect.FromCorners(-1920, -180, -1000, 500);
        Assert.Equal(new PixelRect(-1920, -180, 920, 680), r);
    }

    [Fact]
    public void FromCorners_of_a_single_point_is_empty()
    {
        var r = PixelRect.FromCorners(50, 50, 50, 50);
        Assert.Equal(0, r.Width);
        Assert.Equal(0, r.Height);
        Assert.True(r.IsEmpty);
    }

    [Fact]
    public void One_pixel_rect_is_not_empty()
    {
        var r = PixelRect.FromCorners(50, 50, 51, 51);
        Assert.Equal(new PixelRect(50, 50, 1, 1), r);
        Assert.False(r.IsEmpty);
    }

    [Fact]
    public void Intersect_returns_the_overlap()
    {
        var a = new PixelRect(0, 0, 100, 100);
        var b = new PixelRect(50, 50, 100, 100);
        Assert.Equal(new PixelRect(50, 50, 50, 50), a.Intersect(b));
    }

    [Fact]
    public void Intersect_of_disjoint_rects_is_empty()
    {
        var a = new PixelRect(0, 0, 10, 10);
        var b = new PixelRect(100, 100, 10, 10);
        Assert.True(a.Intersect(b).IsEmpty);
    }

    [Fact]
    public void ClampTo_pulls_a_rect_inside_the_bounds()
    {
        var bounds = new PixelRect(-1920, 0, 3840, 1080);
        var r = new PixelRect(-2500, -50, 1000, 2000);
        Assert.Equal(new PixelRect(-1920, 0, 580, 1080), r.ClampTo(bounds));
    }
}
