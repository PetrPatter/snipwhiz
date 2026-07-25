namespace Snipwhiz.Core.Geometry;

/// <summary>
/// Physical-pixel to DIP conversion. Scale is injected, never read from the OS here —
/// that is what keeps these pure and testable.
/// </summary>
public static class Dpi
{
    public static double PhysicalToDip(int physical, double scale) => physical / scale;

    public static int DipToPhysical(double dip, double scale)
        => (int)Math.Round(dip * scale, MidpointRounding.AwayFromZero);
}
