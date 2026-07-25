using System.Windows.Threading;
using Microsoft.Win32;
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
    public SelectionController Selection { get; private set; } = null!;

    public event Action<PixelRect>? Committed;
    public event Action? Cancelled;

    public CaptureSession(FrozenDesktop frozen)
    {
        _frozen = frozen;
        _watchdog = new DispatcherTimer { Interval = WatchdogTimeout };
        _watchdog.Tick += (_, _) => Cancel();

        // The frozen buffer is invalid the moment the monitor topology changes
        // (hot-unplug, sleep/resume re-detect, resolution change). Bail out rather
        // than let the overlay keep showing a snapshot of a desktop that no longer
        // matches reality.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    private void OnDisplaySettingsChanged(object? sender, EventArgs e) => Cancel();

    /// <returns>False when the overlay could not be brought to the foreground.</returns>
    public bool Start()
    {
        // Armed before the first opaque topmost window appears, so it is a real
        // backstop even if something below throws before reaching the catch.
        _watchdog.Start();
        try
        {
            var cursor = _frozen.Cursor;
            var active = _frozen.Desktop.MonitorAt(cursor.X, cursor.Y)
                      ?? _frozen.Desktop.Monitors.First(m => m.IsPrimary);

            foreach (var monitor in _frozen.Desktop.Monitors)
            {
                var overlay = new OverlayWindow(_frozen, monitor);
                overlay.Cancelled += Cancel;
                overlay.AttachLoupe(_frozen);
                _overlays.Add(overlay);
                overlay.ShowAt(activate: monitor.DeviceName == active.DeviceName);
            }

            Selection = new SelectionController(this);

            // Before the first real BeginDrag, every overlay is showing the plain
            // frozen screenshot with no dim layer added at all — RenderSelection has
            // never run on it. Only once a drag has actually started (Current becomes
            // non-null, or is null because BeginDrag was called) is there a selection
            // render in flight that a later DPI change (NeedsRedraw, below) needs to
            // keep in sync. Guarding on this stops a DPI event that lands before any
            // drag from prematurely dimming an overlay the user hasn't interacted with.
            var selectionEngaged = false;
            Selection.Changed += rect =>
            {
                selectionEngaged = true;
                foreach (var o in _overlays) o.RenderSelection(rect);
            };

            foreach (var overlay in _overlays)
            {
                overlay.DragStarted += (x, y) => Selection.BeginDrag(x, y);
                overlay.PointerMoved += (x, y) =>
                {
                    Selection.UpdateDrag(x, y);
                    foreach (var o in _overlays) o.MoveLoupe(x, y);
                };
                overlay.DragEnded += () =>
                {
                    if (Selection.EndDrag() is { } rect) Commit(rect);
                };
                overlay.NeedsRedraw += () =>
                {
                    if (selectionEngaged) overlay.RenderSelection(Selection.Current);
                };
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

            return true;
        }
        catch
        {
            // Covers OverlayWindow construction, ShowAt (native HWND creation and
            // the initial Show()/layout pass), and designated.Activate(): if any of
            // those throw mid-loop, no overlay has focus (Activate() never reached),
            // so Esc goes nowhere and only right-click remains — and without this
            // catch, the overlays stay up with nothing torn down. Note this does
            // NOT cover RenderFrozenSlice: OverlayWindow defers that (and the
            // physical-bounds/DPI invariant check) to DispatcherPriority.ApplicationIdle
            // via EnsureCorrectRendering, which runs after Start() has already
            // returned — that path has its own try/catch for the same reason.
            Cancel();
            throw;
        }
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
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _watchdog.Stop();
        foreach (var overlay in _overlays) overlay.Close();
        _overlays.Clear();
    }

    public void Dispose()
    {
        _closed = true;
        CloseOverlays();
    }
}
