using Snipwhiz.Core.Geometry;
using Snipwhiz.Core.Monitors;
using Xunit;

namespace Snipwhiz.Core.Tests.Monitors;

/// <summary>
/// Enumerate() needs a real display, but the HRESULT handling that used to be
/// missing does not — and it is the part that silently poisoned every coordinate
/// conversion for a monitor.
/// </summary>
public class MonitorEnumeratorTests
{
    [Theory]
    [InlineData(96u, 1.0)]
    [InlineData(120u, 1.25)]
    [InlineData(144u, 1.5)]
    public void A_successful_call_yields_the_reported_scale(uint dpiX, double expected) =>
        Assert.Equal(expected, MonitorEnumerator.ScaleFrom(succeeded: true, dpiX));

    [Fact]
    public void A_failed_call_falls_back_to_100_percent() =>
        // dpiX is left at 0 on failure. Without the fallback that is Scale 0.
        Assert.Equal(1.0, MonitorEnumerator.ScaleFrom(succeeded: false, dpiX: 0));

    [Fact]
    public void A_success_that_still_reports_zero_dpi_falls_back_to_100_percent() =>
        Assert.Equal(1.0, MonitorEnumerator.ScaleFrom(succeeded: true, dpiX: 0));

    [Fact]
    public void The_fallback_keeps_DIP_conversion_finite()
    {
        // This is the actual damage a 0 scale did: every conversion became Infinity.
        var scale = MonitorEnumerator.ScaleFrom(succeeded: false, dpiX: 0);
        Assert.Equal(1920.0, Dpi.PhysicalToDip(1920, scale));
    }
}
