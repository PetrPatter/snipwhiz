using System.Windows.Threading;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Geometry;

namespace Snipwhiz.App;

/// <summary>
/// Owns one overlay per monitor for the life of a capture. Guarantees there is
/// always a way out: if activation is refused, or nothing responds, the session
/// tears itself down rather than leaving an unfocusable fullscreen window.
/// </summary>
public sealed class CaptureSession : IDisposable
{
    private static readonly TimeSpan WatchdogTimeout = TimeSpan.FromSeconds(60);

    private readonly List<OverlayWindow> _overlays = [];
    private readonly FrozenDesktop _frozen;
    private readonly DispatcherTimer _watchdog;
    private bool _closed;

    public FrozenDesktop Frozen => _frozen;
    public IReadOnlyList<OverlayWindow> Overlays => _overlays;

    public event Action<PixelRect>? Committed;
    public event Action? Cancelled;

    public CaptureSession(FrozenDesktop frozen)
    {
        _frozen = frozen;
        _watchdog = new DispatcherTimer { Interval = WatchdogTimeout };
        _watchdog.Tick += (_, _) => Cancel();
    }

    /// <returns>False when the overlay could not be brought to the foreground.</returns>
    public bool Start()
    {
        var cursor = _frozen.Cursor;
        var active = _frozen.Desktop.MonitorAt(cursor.X, cursor.Y)
                  ?? _frozen.Desktop.Monitors.First(m => m.IsPrimary);

        foreach (var monitor in _frozen.Desktop.Monitors)
        {
            var overlay = new OverlayWindow(_frozen, monitor);
            overlay.Cancelled += Cancel;
            _overlays.Add(overlay);
            overlay.ShowAt(activate: monitor.DeviceName == active.DeviceName);
        }

        var designated = _overlays.First(o => o.Monitor.DeviceName == active.DeviceName);
        designated.Activate();

        // SetForegroundWindow can be refused even for a hotkey-driven process.
        // Aborting is the only safe response — an opaque fullscreen window that
        // will not take a keystroke is the worst bug we could ship.
        if (!designated.IsActive)
        {
            Cancel();
            return false;
        }

        _watchdog.Start();
        return true;
    }

    public void Commit(PixelRect region)
    {
        if (_closed) return;
        _closed = true;
        CloseOverlays();
        Committed?.Invoke(region);
    }

    public void Cancel()
    {
        if (_closed) return;
        _closed = true;
        CloseOverlays();
        Cancelled?.Invoke();
    }

    private void CloseOverlays()
    {
        _watchdog.Stop();
        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear();
    }

    public void Dispose() => CloseOverlays();
}
