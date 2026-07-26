namespace Snipwhiz.Core.Capture;

/// <summary>
/// Seam for testing and for swapping in DXGI Desktop Duplication if the
/// latency gate in the plan's Task 2 fails.
/// </summary>
public interface IDesktopGrabber
{
    FrozenDesktop Grab();
}
