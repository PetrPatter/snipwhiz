namespace Snipwhiz.Core.Geometry;

/// <param name="Bounds">Physical pixels in virtual-screen space.</param>
/// <param name="Scale">1.0 = 100%, 1.5 = 150%, 2.25 = 225%.</param>
public readonly record struct MonitorInfo(
    string DeviceName,
    PixelRect Bounds,
    double Scale,
    bool IsPrimary);
