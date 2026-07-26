using System.Windows;
using System.Windows.Threading;
using Application = System.Windows.Application;
using MessageBox = System.Windows.MessageBox;
using Snipwhiz.App.Library;
using Snipwhiz.Core;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Geometry;
using Snipwhiz.Core.Hotkeys;
using Snipwhiz.Core.Imaging;
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

        // Registered before the tray exists so it covers as much of startup as
        // possible. A failed capture must not take the app down with it — the
        // user would just find Snipwhiz gone with no explanation. Continuing
        // after an unhandled exception can mask a bug, but for a tray utility
        // the user expects to still be there tomorrow, dying silently is worse
        // than continuing with a visible error. Deliberate tradeoff, not an
        // oversight.
        DispatcherUnhandledException += (_, ex) =>
        {
            // Not "Capture failed" any more — this now catches library faults too,
            // and a wrong label sends the user looking in the wrong place.
            _tray?.ShowBalloon("Snipwhiz hit a problem", ex.Exception.Message, isError: true);
            ex.Handled = true;
        };

        try
        {
            var settings = Settings.Load(_root);
            _store = new CaptureStore(_root);
            _pipeline = new CapturePipeline(_store);

            _tray = new TrayHost(settings, _root);
            _tray.FullscreenRequested += CaptureFullscreen;
            _tray.RegionRequested += CaptureRegion;
            _tray.LibraryRequested += ShowLibrary;
            _tray.CancelRequested += () => _session?.Cancel();
            _tray.ExitRequested += Shutdown;

            _hotkeys = new HotkeyService();
            _hotkeys.Pressed += id =>
            {
                if (id == HotkeyId.Fullscreen) CaptureFullscreen();
                else if (id is HotkeyId.Region or HotkeyId.PrintScreenRegion) CaptureRegion();
                else if (id == HotkeyId.Library) ShowLibrary();
            };

            RegisterHotkeys();
            _tray.ShowBalloon("Snipwhiz is running", "Press Ctrl+Shift+2 to capture the screen.");

            OfferPrintScreenTakeover(settings);
        }
        catch (Exception ex)
        {
            // Nothing may outlive a failed startup — a visible tray icon with no
            // process behind it cannot be dismissed by the user.
            _hotkeys?.Dispose();
            _tray?.Dispose();
            _store?.Dispose();

            MessageBox.Show(
                $"Snipwhiz could not start.\n\n{ex.Message}",
                "Snipwhiz", MessageBoxButton.OK, MessageBoxImage.Error);

            Shutdown(1);
        }
    }

    private void RegisterHotkeys()
    {
        const uint vk1 = 0x31, vk2 = 0x32, vkL = 0x4C;   // '1', '2' and 'L'
        var mods = HotkeyService.ModControl | HotkeyService.ModShift;

        if (!_hotkeys!.TryRegister(HotkeyId.Region, mods, vk1))
            _tray!.ShowBalloon("Hotkey unavailable",
                "Ctrl+Shift+1 is held by another application. Use the tray menu instead.", isError: true);

        if (!_hotkeys.TryRegister(HotkeyId.Fullscreen, mods, vk2))
            _tray!.ShowBalloon("Hotkey unavailable",
                "Ctrl+Shift+2 is held by another application. Use the tray menu instead.", isError: true);

        if (!_hotkeys.TryRegister(HotkeyId.Library, mods, vkL))
            _tray!.ShowBalloon("Hotkey unavailable",
                "Ctrl+Shift+L is held by another application. Open the library from the tray menu instead.",
                isError: true);
    }

    private LibraryWindow? _library;

    private ThumbnailCache? _thumbnails;

    private void ShowLibrary()
    {
        _thumbnails ??= new ThumbnailCache(_store!);
        _library ??= new LibraryWindow(_store!, _thumbnails);
        _library.Reveal();
    }

    private bool _libraryHiddenForCapture;

    /// <summary>
    /// The frozen desktop is grabbed from the live screen, so a visible library
    /// window would be captured sitting on top of whatever the user actually
    /// wanted. Hide it first — and always put it back, including on every abort
    /// path, or a cancelled capture silently loses the user's window.
    /// </summary>
    private void HideLibraryForCapture()
    {
        _libraryHiddenForCapture = _library is { IsVisible: true };
        if (!_libraryHiddenForCapture) return;

        _library!.Hide();

        // Hide() only queues the work. Without letting the dispatcher reach Render
        // the grab can still find the window on screen — the negative control for
        // this is in the task report.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
    }

    private void RestoreLibraryAfterCapture()
    {
        if (!_libraryHiddenForCapture) return;
        _libraryHiddenForCapture = false;
        _library?.Reveal(activate: false);
    }

    private void OfferPrintScreenTakeover(Settings settings)
    {
        if (settings.PrintScreenPromptAnswered) return;

        if (!PrintScreenTakeover.IsSnippingToolBound())
        {
            // Nothing to take over — claim it directly. Mark the prompt answered even
            // though no prompt was shown: this branch must not re-run on every startup,
            // or a user who was never asked gets an error balloon forever when another
            // app holds the key.
            settings.PrintScreenPromptAnswered = true;
            TryClaimPrintScreen(settings);
            return;
        }

        var answer = MessageBox.Show(
            "Use PrintScreen for Snipwhiz?\n\n" +
            "This turns off the Windows Snipping Tool shortcut. You can change it back " +
            "in Settings > Accessibility > Keyboard at any time.\n\n" +
            "Snipwhiz already works with Ctrl+Shift+1 and Ctrl+Shift+2.",
            "Snipwhiz",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        settings.PrintScreenPromptAnswered = true;

        if (answer == MessageBoxResult.Yes)
        {
            PrintScreenTakeover.Release();
            TryClaimPrintScreen(settings);   // saves settings, including PrintScreenPromptAnswered
        }
        else
        {
            settings.Save(_root);
        }
    }

    private void TryClaimPrintScreen(Settings settings)
    {
        var claimed = _hotkeys!.TryRegister(
            HotkeyId.PrintScreenRegion, 0, HotkeyService.VkPrintScreen);

        settings.PrintScreenTakenOver = claimed;
        settings.Save(_root);

        if (claimed)
        {
            _tray!.ShowBalloon("PrintScreen is almost ready",
                "Sign out and back in to finish switching PrintScreen away from Snipping Tool.");
        }
        else
        {
            var holder = PrintScreenTakeover.DescribeLikelyHolder();
            _tray!.ShowBalloon("PrintScreen unavailable",
                holder is null
                    ? "Another application is holding the PrintScreen key. Ctrl+Shift+1 still works."
                    : $"{holder} is holding the PrintScreen key. Ctrl+Shift+1 still works.",
                isError: true);
        }
    }

    private void CaptureFullscreen()
    {
        // Ctrl+Shift+2 is one digit away from Ctrl+Shift+1, so it gets pressed by
        // mistake while the overlays are up. Without this guard it grabs the screen
        // *including* the dim layer, selection border and loupe, then writes that
        // immutably to disk with a DB row and overwrites the clipboard with it.
        if (_session is not null)
        {
            _tray!.ShowBalloon("Capture already in progress",
                "Finish the region selection, or press Esc or right-click to cancel it first.");
            return;
        }

        var (app, title) = ForegroundWindow.Describe();

        HideLibraryForCapture();
        try
        {
            var frozen = _grabber.Grab();

            var cursor = frozen.Cursor;
            var monitor = frozen.Desktop.MonitorAt(cursor.X, cursor.Y)
                       ?? frozen.Desktop.Monitors.First(m => m.IsPrimary);

            Report(_pipeline!.Complete(frozen, monitor.Bounds, app, title));
        }
        finally
        {
            RestoreLibraryAfterCapture();
        }
    }

    private CaptureSession? _session;

    private void CaptureRegion()
    {
        if (_session is not null) return;   // a capture is already in flight

        var (app, title) = ForegroundWindow.Describe();

        HideLibraryForCapture();
        var frozen = _grabber.Grab();

        // Manual, re-auditable 1:1-rendering verification harness (see
        // Diagnostics.OverlayVerification for how to run it, including the
        // required negative control). Completely inert — one env-var read, no
        // behavior change — unless explicitly enabled; does not touch _session.
        if (Diagnostics.OverlayVerification.IsEnabled)
        {
            Diagnostics.OverlayVerification.Run(frozen);
            return;
        }

        _session = new CaptureSession(frozen);
        // Abort() raises Aborted and then Cancel(), so every non-commit exit —
        // Esc, right-click, watchdog, display change, focus refusal — lands here.
        _session.Cancelled += () =>
        {
            _session?.Dispose();
            _session = null;
            RestoreLibraryAfterCapture();
        };

        // Esc and right-click stay silent — the user knows they cancelled. Everything
        // else that closes the overlay does so for a reason the user cannot see, and
        // §6 says silent failure is not acceptable.
        _session.Aborted += reason => _tray!.ShowBalloon("Capture cancelled", reason, isError: true);
        _session.Committed += region =>
        {
            // Complete() can throw (disk full, DB locked, clipboard denied — all
            // realistic) and unwind to DispatcherUnhandledException, which swallows
            // it. Without the finally, _session would stay non-null forever,
            // making the "already in flight" guard above a permanent no-op for
            // Ctrl+Shift+1 while Ctrl+Shift+2 kept working — silent, confusing
            // breakage for the rest of the process's life.
            try
            {
                var outcome = _pipeline!.Complete(frozen, region, app, title);
                Report(outcome);
            }
            finally
            {
                _session?.Dispose();
                _session = null;
                RestoreLibraryAfterCapture();
            }
        };

        if (!_session.Start())
        {
            _session = null;
            RestoreLibraryAfterCapture();
            _tray!.ShowBalloon("Capture cancelled",
                "Windows would not allow the capture overlay to take focus. Try again.", isError: true);
        }
    }

    private void Report(CaptureOutcome outcome)
    {
        // Only a successful save has a record. CaptureOutcome.Record is null
        // whenever the disk or database write failed, and inserting a tile for one
        // of those would show a capture that has neither a row nor a file.
        if (outcome.Record is not null) _library?.OnCaptureCompleted(outcome.Record);

        if (outcome.Warning is not null)
            _tray!.ShowBalloon("Capture problem", outcome.Warning, isError: !outcome.ClipboardOk);
        else
            _tray!.ShowBalloon("Copied", $"{outcome.Record!.Width} x {outcome.Record.Height} copied to the clipboard.");
    }

    protected override void OnExit(ExitEventArgs e)
    {
        // OnClosing refuses every close so the window survives being dismissed.
        // Shutdown is the one time that has to be overridden.
        if (_library is not null)
        {
            _library.AllowClose = true;
            _library.Close();
        }
        _session?.Dispose();
        _hotkeys?.Dispose();
        _tray?.Dispose();
        _store?.Dispose();
        _instanceLock?.Dispose();
        base.OnExit(e);
    }
}
