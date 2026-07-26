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
    private readonly Dispatcher _dispatcher = Dispatcher.CurrentDispatcher;
    private bool _closed;

    public FrozenDesktop Frozen => _frozen;
    public IReadOnlyList<OverlayWindow> Overlays => _overlays;
    public SelectionController Selection { get; private set; } = null!;

    public event Action<PixelRect>? Committed;
    public event Action? Cancelled;

    /// <summary>
    /// Raised with a short reason whenever the session tears itself down for a
    /// cause the user did not ask for. Esc and right-click are deliberately silent —
    /// the user already knows they cancelled — but a watchdog timeout or a display
    /// change must be reported, or the overlay just vanishes with no explanation.
    /// </summary>
    public event Action<string>? Aborted;

    public CaptureSession(FrozenDesktop frozen)
    {
        _frozen = frozen;
        _watchdog = new DispatcherTimer { Interval = WatchdogTimeout };
        _watchdog.Tick += (_, _) => Abort("The capture overlay closed itself after 60 seconds without input.");

        // The frozen buffer is invalid the moment the monitor topology changes
        // (hot-unplug, sleep/resume re-detect, resolution change). Bail out rather
        // than let the overlay keep showing a snapshot of a desktop that no longer
        // matches reality.
        SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
    }

    // SystemEvents raises this on its own pump thread (".NET System Events"), never
    // the UI thread. Window.Close() from there throws "the calling thread cannot
    // access this object", and SystemEvents swallows that exception — which used to
    // leave the overlays up with every escape route already latched shut.
    private void OnDisplaySettingsChanged(object? sender, EventArgs e) =>
        _dispatcher.BeginInvoke(new Action(() =>
            Abort("The display configuration changed, so the capture was cancelled.")));

    /// <summary>Cancels and reports why. Silent when the session is already finished.</summary>
    private void Abort(string reason)
    {
        if (_closed) return;
        Aborted?.Invoke(reason);
        Cancel();
    }

    /// <summary>Restarts the 60 s idle watchdog. §4.11 counts 60 s without input, not since Start().</summary>
    private void ResetWatchdog()
    {
        if (_closed) return;
        _watchdog.Stop();
        _watchdog.Start();
    }

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
                overlay.DragStarted += (x, y) =>
                {
                    ResetWatchdog();
                    Selection.BeginDrag(x, y);
                };
                overlay.PointerMoved += (x, y) =>
                {
                    ResetWatchdog();
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

    // Commit and Cancel deliberately do NOT latch _closed before doing the work.
    // That shape is what turned one recoverable throw inside CloseOverlays into an
    // unrecoverable trap: the latch was already shut, the watchdog already stopped,
    // and Cancelled had not fired — so Esc, right-click, the tray menu, the watchdog
    // and even completing a drag all became no-ops behind opaque fullscreen windows.
    // The latch and the notification now both happen in a finally, so no exception
    // anywhere in teardown can leave the session both un-torn-down and un-exitable.

    public void Commit(PixelRect region)
    {
        if (_closed) return;
        try
        {
            CloseOverlays();
        }
        finally
        {
            _closed = true;
            Committed?.Invoke(region);
        }
    }

    public void Cancel()
    {
        if (_closed) return;
        try
        {
            CloseOverlays();
        }
        finally
        {
            _closed = true;
            Cancelled?.Invoke();
        }
    }

    private void CloseOverlays()
    {
        SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
        _watchdog.Stop();

        // Snapshot and clear first: Close() raises events and can re-enter this
        // path, and a second pass over a list being mutated would throw out of the
        // loop and strand whatever had not been closed yet.
        var overlays = _overlays.ToArray();
        _overlays.Clear();

        foreach (var overlay in overlays)
        {
            // One window refusing to close must never prevent the others closing.
            // There is nothing useful to do with the exception here — the session
            // is already going away — and rethrowing would defeat the whole point.
            try { overlay.Close(); }
            catch (Exception) { }
        }
    }

    public void Dispose()
    {
        try
        {
            CloseOverlays();
        }
        finally
        {
            _closed = true;
        }
    }
}
