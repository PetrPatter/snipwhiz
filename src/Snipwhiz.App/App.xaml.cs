using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
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

    /// <summary>
    /// The single instance everything shares — the tray writes autostart into it,
    /// the editor's style pill writes tool defaults. Two loaded copies would each
    /// save their own stale view of the other's fields.
    /// </summary>
    private Settings? _settings;
    private readonly BitBltGrabber _grabber = new();
    // Overridable so verification runs against a throwaway library instead of the
    // user's real captures. Unset in normal use.
    private readonly string _root = CaptureStore.ResolveRoot();

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
            var settings = _settings = Settings.Load(_root);
            _store = new CaptureStore(_root);
            _pipeline = new CapturePipeline(_store);

            // Inert unless SNIPWHIZ_SEED is set, and refuses to run against the
            // real library. See Diagnostics.LibrarySeed.
            Diagnostics.LibrarySeed.RunIfRequested(_store);

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

            // Stands up its own window and closes it; nothing else in the app is
            // involved, so it runs before the library gates and returns.
            if (Diagnostics.CanvasVerification.IsEnabled)
            {
                Diagnostics.CanvasVerification.RunIfRequested();
                return;
            }

            // Never returns; its driver kills this process. Ahead of the other gates
            // because it must not stand up a window or touch the library.
            if (Diagnostics.SettingsWriteVerification.IsEnabled)
            {
                Diagnostics.SettingsWriteVerification.RunIfRequested(_root);
                return;
            }

            if (Diagnostics.TextSeamVerification.IsEnabled)
            {
                Diagnostics.TextSeamVerification.RunIfRequested();
                return;
            }

            if (Diagnostics.WysiwygVerification.IsEnabled)
            {
                Diagnostics.WysiwygVerification.RunIfRequested();
                return;
            }

            if (Diagnostics.EditorMemoryVerification.IsEnabled)
            {
                Diagnostics.EditorMemoryVerification.RunIfRequested(_store);
                return;
            }

            // The grid and resize gates drive the window themselves, so they need
            // it open without waiting for someone to press the hotkey.
            if (Diagnostics.GridVerification.IsEnabled || Diagnostics.ResizeVerification.IsEnabled)
            {
                ShowLibrary();
                return;
            }

            if (settings.FirstRunShown)
            {
                _tray.ShowBalloon("Snipwhiz is running", "Press Ctrl+Shift+2 to capture the screen.");
                OfferPrintScreenTakeover(settings);
            }
            else
            {
                ShowFirstRun(settings);
            }

            StartUpdateCheck();
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
    private Editor.SavePipeline? _saves;

    private void ShowLibrary()
    {
        _thumbnails ??= new ThumbnailCache(_store!);
        if (_library is null)
        {
            _library = new LibraryWindow(_store!, _thumbnails, _settings!, _root);
            _library.EditRequested += ShowEditor;

            _saves = new Editor.SavePipeline(_store!, _thumbnails, Dispatcher);
            _saves.Saved += saved => _library!.OnEditSaved(saved);
            _saves.RenderFailed += (_, e) => _tray?.ShowBalloon(
                "Couldn't render the edited image",
                "Your annotations are saved. The library will show the original until the next save.",
                isError: true);

            _library.EditorSaveRequested += (record, document, source) =>
                _saves!.Save(record, document, source);
            _library.EditorUrgentSaveRequested += (record, document) =>
                _saves!.SaveProjectNow(record, document);
        }
        _library.Reveal();
    }

    /// <summary>
    /// Opens a capture in the editor, decoding the <b>original</b> rather than the
    /// display image: the editor edits the capture, and the flattened render is an
    /// output of that, not an input to it.
    /// </summary>
    private void ShowEditor(CaptureRecord record)
    {
        BitmapSource source;
        try
        {
            using var stream = File.OpenRead(_store!.Assets.Original(record));
            var frame = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
            frame.Freeze();
            source = frame;
        }
        catch (Exception e) when (e is IOException or NotSupportedException or ArgumentException)
        {
            // Spec §4.18: opening an editor over a blank canvas and letting someone
            // annotate nothing is worse than refusing.
            _tray?.ShowBalloon("Can't edit this capture", "The image file is missing or unreadable.", isError: true);
            return;
        }

        _library?.ShowEditor(record, source);
    }

    private bool _libraryHiddenForCapture;

    /// <summary>
    /// The frozen desktop is grabbed from the live screen, so a visible library
    /// window would be captured sitting on top of whatever the user actually
    /// wanted. Hide it first — and always put it back, including on every abort
    /// path, or a cancelled capture silently loses the user's window.
    ///
    /// <para><b>This covers the editor too, without knowing about it.</b> Spec §4.17
    /// called for generalising hide-for-capture to every app window, because an open
    /// editor would otherwise land in the grab — worst with <i>Open editor after
    /// capture</i> on, where each capture would photograph the editor the previous
    /// one opened. Making the editor a screen inside this window rather than a
    /// window of its own (§4.16) removed the problem instead of solving it: there
    /// is only one window to hide.</para>
    /// </summary>
    private void HideLibraryForCapture()
    {
        _libraryHiddenForCapture = _library is { IsVisible: true };
        if (!_libraryHiddenForCapture) return;

        _library!.Hide();

        // Hide only queues the work, and the dispatcher reaching Render says
        // nothing about what the compositor has actually put on screen — which is
        // what the grab reads. Pumping alone left a translucent library in the
        // capture; DwmFlush waits for the frame that no longer contains it.
        Dispatcher.Invoke(() => { }, DispatcherPriority.Render);
        Mica.WaitForCompositor();
    }

    private void RestoreLibraryAfterCapture()
    {
        if (!_libraryHiddenForCapture) return;
        _libraryHiddenForCapture = false;
        _library?.Reveal(activate: false);
    }

    /// <summary>
    /// The only window this app has ever opened without being asked, and it opens
    /// once.
    ///
    /// <para>Non-modal on purpose. <c>ShowDialog</c> here would block
    /// <c>OnStartup</c> from returning, and the hotkeys are already registered by
    /// this point — someone who presses Ctrl+Shift+1 while reading the window that
    /// just told them to must get a capture, not a beep.</para>
    ///
    /// <para>The window collects answers; every effect is applied here, through the
    /// paths that already own it. Autostart in particular goes through the tray's
    /// menu item, because the registry write has one owner and the tick has to stay
    /// honest about what is on disk.</para>
    /// </summary>
    private void ShowFirstRun(Settings settings)
    {
        var snippingToolHolds = PrintScreenTakeover.IsSnippingToolBound();
        var window = new FirstRunWindow(offerPrintScreen: snippingToolHolds);

        window.Closed += (_, _) =>
        {
            // Set before anything that saves, because both branches below write the
            // file and neither should leave this flag behind. Dismissing the window
            // by any means counts as having seen it; there is no way to be shown it
            // twice by closing it wrong.
            settings.FirstRunShown = true;
            settings.PrintScreenPromptAnswered = true;

            if (window.StartWithWindows) _tray!.Autostart = true;   // writes HKCU, saves

            // The same two outcomes OfferPrintScreenTakeover reaches, from a
            // checkbox rather than a message box: take the key when it was offered
            // and wanted, or claim it silently when nothing was holding it.
            if (snippingToolHolds && window.TakeOverPrintScreen) PrintScreenTakeover.Release();

            if (!snippingToolHolds || window.TakeOverPrintScreen) TryClaimPrintScreen(settings);
            else settings.Save(_root);
        };

        window.Show();
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

    private readonly Update.Updater _updater = new();

    /// <summary>
    /// Kicks off the once-per-launch update check, off the startup path entirely.
    ///
    /// <para><b>Nothing about the tray or the hotkey may wait on this.</b> Spec 1
    /// spent real effort on capture being instant and an update check on the startup
    /// path would spend it back — so this is queued at
    /// <see cref="DispatcherPriority.ApplicationIdle"/>, which cannot run until
    /// everything above has, and then goes to a background thread anyway. The check
    /// itself never throws; see <see cref="Update.Updater"/>.</para>
    /// </summary>
    private void StartUpdateCheck()
    {
        _updater.Ready += () => _tray?.ShowUpdateReady();

        _tray!.RestartForUpdateRequested += () =>
        {
            _restartAfterUpdate = true;
            Shutdown();
        };

        Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle,
            new Action(() => _ = _updater.CheckAsync()));
    }

    /// <summary>
    /// Whether the user asked to restart, rather than simply closing the app. An
    /// ordinary exit applies the update and stays exited: relaunching a tray app
    /// somebody has just quit is not an update, it is an argument.
    /// </summary>
    private bool _restartAfterUpdate;

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

        // Last, and after the single-instance lock is released. Velopack's updater
        // waits for this process to end before it touches the install directory, and
        // the new version will want that mutex the moment it starts.
        _updater.Apply(restart: _restartAfterUpdate);

        base.OnExit(e);
    }
}
