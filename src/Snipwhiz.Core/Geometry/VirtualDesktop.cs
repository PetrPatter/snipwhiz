namespace Snipwhiz.Core.Geometry;

/// <summary>
/// The set of displays and their union. The union may be larger than the covered
/// area: an L-shaped or offset arrangement leaves gaps that belong to no display.
/// </summary>
public sealed class VirtualDesktop
{
    public IReadOnlyList<MonitorInfo> Monitors { get; }
    public PixelRect Bounds { get; }

    private VirtualDesktop(IReadOnlyList<MonitorInfo> monitors, PixelRect bounds)
    {
        Monitors = monitors;
        Bounds = bounds;
    }

    public static VirtualDesktop FromMonitors(IEnumerable<MonitorInfo> monitors)
    {
        var list = monitors.ToArray();
        if (list.Length == 0) throw new ArgumentException("At least one monitor is required.", nameof(monitors));

        var left   = list.Min(m => m.Bounds.X);
        var top    = list.Min(m => m.Bounds.Y);
        var right  = list.Max(m => m.Bounds.Right);
        var bottom = list.Max(m => m.Bounds.Bottom);

        return new VirtualDesktop(list, new PixelRect(left, top, right - left, bottom - top));
    }

    public MonitorInfo? MonitorAt(int x, int y)
    {
        foreach (var m in Monitors)
            if (m.Bounds.Contains(x, y)) return m;
        return null;
    }

    public bool IsCovered(int x, int y) => MonitorAt(x, y) is not null;
}
