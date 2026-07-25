using System.Windows;
using Application = System.Windows.Application;
using Snipwhiz.Core;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Geometry;
using Snipwhiz.Core.Hotkeys;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App;

public partial class App : Application
{
    private Mutex? _instanceLock;
    private TrayHost? _tray;
    private HotkeyService? _hotkeys;
    private CaptureStore? _store;
    private CapturePipeline? _pipeline;
    private readonly BitBltGrabber _grabber = new();
    private string _root = CaptureStore.DefaultRoot;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _instanceLock = new Mutex(initiallyOwned: true, @"Local\Snipwhiz.SingleInstance", out var isFirst);
        if (!isFirst)
        {
            Shutdown();
            return;
        }

        var settings = Settings.Load(_root);
        _store = new CaptureStore(_root);
        _pipeline = new CapturePipeline(_store);

        _tray = new TrayHost(settings, _root);
        _tray.FullscreenRequested += CaptureFullscreen;
        _tray.RegionRequested += CaptureFullscreen;   // replaced by the overlay in Task 9
        _tray.ExitRequested += Shutdown;

        _hotkeys = new HotkeyService();
        _hotkeys.Pressed += id =>
        {
            if (id is HotkeyId.Fullscreen or HotkeyId.Region) CaptureFullscreen();
        };

        RegisterHotkeys();
        _tray.ShowBalloon("Snipwhiz is running", "Press Ctrl+Shift+2 to capture the screen.");
    }

    private void RegisterHotkeys()
    {
        const uint vk1 = 0x31, vk2 = 0x32;   // '1' and '2'
        var mods = HotkeyService.ModControl | HotkeyService.ModShift;

        if (!_hotkeys!.TryRegister(HotkeyId.Region, mods, vk1))
            _tray!.ShowBalloon("Hotkey unavailable",
                "Ctrl+Shift+1 is held by another application. Use the tray menu instead.", isError: true);

        if (!_hotkeys.TryRegister(HotkeyId.Fullscreen, mods, vk2))
            _tray!.ShowBalloon("Hotkey unavailable",
                "Ctrl+Shift+2 is held by another application. Use the tray menu instead.", isError: true);
    }

    private void CaptureFullscreen()
    {
        var (app, title) = ForegroundWindow.Describe();
        var frozen = _grabber.Grab();

        var cursor = frozen.Cursor;
        var monitor = frozen.Desktop.MonitorAt(cursor.X, cursor.Y)
                   ?? frozen.Desktop.Monitors.First(m => m.IsPrimary);

        Report(_pipeline!.Complete(frozen, monitor.Bounds, app, title));
    }

    private void Report(CaptureOutcome outcome)
    {
        if (outcome.Warning is not null)
            _tray!.ShowBalloon("Capture problem", outcome.Warning, isError: !outcome.ClipboardOk);
        else
            _tray!.ShowBalloon("Copied", $"{outcome.Record!.Width} x {outcome.Record.Height} copied to the clipboard.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _store?.Dispose();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
