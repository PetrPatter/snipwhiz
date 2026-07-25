using Snipwhiz.Core.Geometry;
using Xunit;

namespace Snipwhiz.Core.Tests.Geometry;

public class DpiTests
{
    [Theory]
    [InlineData(100, 1.0, 100)]
    [InlineData(150, 1.5, 100)]
    [InlineData(225, 2.25, 100)]
    public void PhysicalToDip_divides_by_scale(int physical, double scale, double expected)
        => Assert.Equal(expected, Dpi.PhysicalToDip(physical, scale), 6);

    [Theory]
    [InlineData(100.0, 1.5, 150)]
    [InlineData(100.0, 2.25, 225)]
    public void DipToPhysical_multiplies_and_rounds(double dip, double scale, int expected)
        => Assert.Equal(expected, Dpi.DipToPhysical(dip, scale));

    [Fact]
    public void DipToPhysical_rounds_half_away_from_zero()
    {
        // 33.5 physical px at 150% is 22.333 DIP; round-tripping must not drift
        Assert.Equal(101, Dpi.DipToPhysical(67.0, 1.5));
    }

    [Fact]
    public void Round_trip_at_awkward_scale_stays_within_one_pixel()
    {
        for (int px = 0; px < 2000; px++)
        {
            var back = Dpi.DipToPhysical(Dpi.PhysicalToDip(px, 2.25), 2.25);
            Assert.InRange(back - px, -1, 1);
        }
    }
}
