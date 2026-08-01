using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Markup;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snipwhiz.Core;
using Snipwhiz.Core.Clipboard;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Scene;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App.Library;

/// <summary>
/// The library. One instance for the life of the process: closing hides it, so
/// reopening is instant and the grid keeps its state. Only <see cref="App.OnExit"/>
/// really destroys it.
/// </summary>
public partial class LibraryWindow : Window
{
    // Must match CaptureTile's Width plus its right margin, or the column count
    // is computed against a tile width that does not exist.
    private const double TileStride = 252 + 16;

    private readonly CaptureStore _store;
    private readonly ThumbnailCache _thumbnails;
    private readonly LibraryViewModel _model;

    // Passed straight through to the editor's style pill. The one instance the app
    // loaded at startup, not a second copy: two copies means whichever saves last
    // writes its own stale view of everything else back over the file.
    private readonly Settings _settings;
    private readonly string _root;

    public LibraryWindow(CaptureStore store, ThumbnailCache thumbnails, Settings settings, string root)
    {
        _store = store;
        _thumbnails = thumbnails;
        _settings = settings;
        _root = root;
        InitializeComponent();

        _model = new LibraryViewModel(store, thumbnails);
        DataContext = _model;

        _preview = new PreviewView(store);
        _preview.Dismissed += () => RootContent.Visibility = Visibility.Visible;
        _preview.DeleteRequested += Delete;
        _preview.EditRequested += record => EditRequested?.Invoke(record);
        PreviewHost.Content = _preview;

        LibraryTab.Click += (_, _) => ShowLibraryScreen();
        EditTab.Click += (_, _) => { if (_editor?.Record is not null) SetScreen(editing: true); };
        EditTab.IsEnabled = false;

        UndoButton.Click += (_, _) => UndoLastDelete();
        EditorCopyButton.Click += (_, _) => CopyFromEditor();

        // 200 ms after the last keystroke, not on every one — otherwise a typed
        // word runs a query per character and the grid flickers through them.
        _searchDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        _searchDebounce.Tick += (_, _) =>
        {
            _searchDebounce!.Stop();
            _model.Search(SearchBox.Text);
            UpdateEmptyState();
        };
        SearchBox.TextChanged += (_, _) => { _searchDebounce!.Stop(); _searchDebounce.Start(); };

        // One handler for every tile rather than plumbing a click event through
        // the templates: find the tile the click landed in and open it.
        RowsHost.PreviewMouseLeftButtonUp += OnGridClick;

        CaptureTile.RemoveRequested += OnRemoveRequested;
        CaptureTile.CopyRequested += OnCopyRequested;
        CaptureTile.DeleteRequested += Delete;
        Closed += (_, _) =>
        {
            CaptureTile.RemoveRequested -= OnRemoveRequested;
            CaptureTile.CopyRequested -= OnCopyRequested;
            CaptureTile.DeleteRequested -= Delete;
        };

        SourceInitialized += (_, _) =>
        {
            // The XAML sets Background to Transparent so the backdrop can show
            // through. If the compositor refuses it that would leave a see-through
            // window, so the flat fallback is applied here.
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!Mica.TryApply(hwnd))
                Background = (System.Windows.Media.Brush)FindResource("Surface");

            // So hiding for a capture is instant rather than a fade the grab can
            // catch halfway through.
            Mica.DisableTransitions(hwnd);
        };

        Loaded += (_, _) =>
        {
            RowsHost.AddHandler(ScrollViewer.ScrollChangedEvent,
                new ScrollChangedEventHandler(OnScrollChanged));

            RowsHost.ApplyTemplate();
            _scroller = RowsHost.Template.FindName("Scroller", RowsHost) as ScrollViewer;

            // Negative control for the virtualization gate: a plain StackPanel
            // realizes every row, which is exactly what the gate must detect.
            if (Diagnostics.GridVerification.BreakVirtualization)
            {
                RowsHost.ItemsPanel = (ItemsPanelTemplate)XamlReader.Parse(
                    """
                    <ItemsPanelTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation">
                      <StackPanel/>
                    </ItemsPanelTemplate>
                    """);
            }

            ApplyColumns();
            _model.Reload();
            RefreshFooter();

            if (Diagnostics.GridVerification.IsEnabled && _scroller is not null)
                Diagnostics.GridVerification.Sweep(RowsHost, _scroller,
                    () => _model.Count, () => _model.RetainedThumbnails);

            Diagnostics.ResizeVerification.Run(this, RowsHost);
        };

        SizeChanged += (_, _) => ApplyColumns();
    }

    private readonly PreviewView _preview;
    private ScrollViewer? _scroller;
    private readonly DispatcherTimer _searchDebounce;

    /// <summary>
    /// A capture taken while this window is open, inserted without a re-query —
    /// the record is already in hand.
    /// </summary>
    /// <summary>The user asked to edit a capture. App decodes it and calls <see cref="ShowEditor"/>.</summary>
    public event Action<CaptureRecord>? EditRequested;

    private Editor.EditorView? _editor;

    /// <summary>
    /// Switches to the Edit screen, creating it on first use.
    ///
    /// <para>The editor is a view in this window rather than a window of its own:
    /// one taskbar entry, one Mica surface, one Escape chain, one lifetime.</para>
    /// </summary>
    public void ShowEditor(CaptureRecord record, BitmapSource source)
    {
        if (_editor is null)
        {
            _editor = new Editor.EditorView(_store, _settings, _root);
            _editor.ExitRequested += ShowLibraryScreen;
            _editor.CopyRequested += CopyFromEditor;
            _editor.SaveRequested += (r, d, s) => EditorSaveRequested?.Invoke(r, d, s);
            _editor.UrgentSaveRequested += (r, d) => EditorUrgentSaveRequested?.Invoke(r, d);
            EditorHost.Content = _editor;
        }

        _editor.Open(record, source);
        SetScreen(editing: true);
        _editor.FocusCanvas();
    }

    /// <summary>Forwarded from the editor so App can own the save pipeline.</summary>
    public event Action<CaptureRecord, SceneDocument, BitmapSource>? EditorSaveRequested;

    /// <summary>Must complete before the window goes. See <see cref="OnClosing"/>.</summary>
    public event Action<CaptureRecord, SceneDocument>? EditorUrgentSaveRequested;

    /// <summary>
    /// A save finished: point the editor and the grid at the capture as it now
    /// stands, and let the tile re-fetch its thumbnail.
    ///
    /// <para>The cached JPEG was already deleted by the save pipeline. That alone
    /// changes nothing on screen — the tile view model latches once loaded and is
    /// never rebuilt — which is why the refresh is pushed rather than waited for.</para>
    /// </summary>
    public void OnEditSaved(CaptureRecord saved)
    {
        // A copy is waiting on this save. Handing it the saved record rather than
        // the one Copy was pressed against matters: this is the one carrying the
        // FlatPath the render just produced.
        _copyAfterSave?.TrySetResult(saved);

        _editor?.OnSaved(saved);
        if (Diagnostics.RefreshVerification.BreakRefresh) return;
        _model.Replace(saved);
    }

    private void ShowLibraryScreen()
    {
        // Leaving the editor saves. There is no unsaved state to ask about.
        _editor?.Save();
        SetScreen(editing: false);
    }

    private void SetScreen(bool editing)
    {
        RootContent.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        EditorHost.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;

        EditTab.IsEnabled = _editor?.Record is not null;
        EditorCopyButton.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        EditTabMark.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        LibraryTabMark.Visibility = editing ? Visibility.Collapsed : Visibility.Visible;
        EditTab.Foreground = editing ? Ink : InkMuted;
        LibraryTab.Foreground = editing ? InkMuted : Ink;

        if (!editing) _preview.Close();
    }

    private bool IsEditing => EditorHost.Visibility == Visibility.Visible;

    private Brush Ink => (Brush)FindResource("Ink");

    private Brush InkMuted => (Brush)FindResource("InkMuted");

    public void OnCaptureCompleted(CaptureRecord record)
    {
        // Deliberately not gated on IsVisible: the window is hidden at this moment
        // because it hid itself for the capture (§4.14), and the report arrives
        // before it is restored. Gating here would drop every live insert.
        _model.InsertNewest(record);
        UpdateEmptyState();
        RefreshFooter();
    }

    private static readonly TimeSpan UndoWindow = TimeSpan.FromSeconds(5);

    private sealed record PendingDelete(CaptureRecord Record, DispatcherTimer Timer);

    private readonly List<PendingDelete> _pendingDeletes = [];

    /// <summary>
    /// Removes the row immediately and the files only once the undo window has
    /// closed.
    ///
    /// The order is the whole point. Deleting the file up front would make undo a
    /// lie: the row would come back pointing at a PNG that no longer exists, the
    /// tile would reappear, and the capture would be gone. Row-first also means a
    /// crash between the two steps leaves an orphaned file — invisible and
    /// harmless — rather than an orphaned row, which is a visibly broken tile.
    /// </summary>
    /// <summary>
    /// Re-copy a stored capture, from the tile, without opening anything.
    ///
    /// <para>Through <see cref="ClipboardCopier"/> like every other copy in the
    /// app. It resolves the <i>display</i> asset, so a capture that has been
    /// annotated copies with its annotations rather than as the bare original.</para>
    ///
    /// <para>Confirmation goes in the footer rather than a balloon. The plan said
    /// to reuse the tray balloon, which is right for a capture taken while the
    /// window is not on screen and wrong here: this window is focused and under the
    /// pointer, and a notification popping out of the system tray to report
    /// something that happened two inches away is noise.</para>
    /// </summary>
    private async void OnCopyRequested(CaptureRecord record)
    {
        var result = await ClipboardCopier.CopyAsync(_store, record);

        Flash(result switch
        {
            CopyResult.Copied => "Copied to the clipboard",
            CopyResult.ClipboardUnavailable => "Another app is holding the clipboard. Try again.",
            _ => "That capture's file could not be read.",
        });
    }

    private TaskCompletionSource<CaptureRecord>? _copyAfterSave;

    /// <summary>
    /// Copy what is on the editor's canvas, including whatever was drawn a second
    /// ago.
    ///
    /// <para><b>Save first, then copy what the save produced.</b> This is the whole
    /// difficulty of the feature and the reason it is not one line.
    /// <see cref="ClipboardCopier"/> copies the display asset — a file — and the
    /// editor deliberately has no dirty state, writing only on the way out. Copying
    /// the file as it stands would put the previous render on the clipboard and
    /// silently omit the last few annotations, which is worse than having no Copy
    /// button at all: it looks like it worked.</para>
    ///
    /// <para>Every failure path here declines to copy rather than copying something
    /// stale. A render that fails still commits the project, and
    /// <c>Assets.Display</c> then falls back to the un-annotated original — so a
    /// missing <c>FlatPath</c> has to be treated as a failure even though the save
    /// itself succeeded.</para>
    /// </summary>
    private async void CopyFromEditor()
    {
        if (_editor?.Record is null) return;

        var wait = _copyAfterSave = new TaskCompletionSource<CaptureRecord>();
        _editor.Save();

        // A save that never commits must not hang this forever. The pipeline
        // returns without committing when it cannot write the project at all.
        var finished = await Task.WhenAny(wait.Task, Task.Delay(TimeSpan.FromSeconds(15)));
        _copyAfterSave = null;

        if (finished != wait.Task)
        {
            _editor.Flash("Couldn't save the edit, so nothing was copied.");
            return;
        }

        var saved = wait.Task.Result;
        if (saved.FlatPath is null)
        {
            _editor.Flash("Couldn't render the edit, so nothing was copied.");
            return;
        }

        _editor.Flash(await ClipboardCopier.CopyAsync(_store, saved) switch
        {
            CopyResult.Copied => "Copied to the clipboard",
            CopyResult.ClipboardUnavailable => "Another app is holding the clipboard. Try again.",
            _ => "That capture's file could not be read.",
        });
    }

    private DispatcherTimer? _flash;

    /// <summary>
    /// Says something in the footer for a moment, then puts the count back.
    /// Restarted rather than stacked, so copying twice quickly does not restore
    /// the footer while the second message is still up.
    /// </summary>
    private void Flash(string message)
    {
        Footer.Text = message;

        _flash ??= new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.5) };
        _flash.Stop();
        _flash.Tick -= OnFlashElapsed;
        _flash.Tick += OnFlashElapsed;
        _flash.Start();
    }

    private void OnFlashElapsed(object? sender, EventArgs e)
    {
        _flash!.Stop();
        RefreshFooter();
    }

    private void Delete(CaptureRecord record)
    {
        // Trivially reachable: open a capture, go back to Library, delete it. The
        // editor must let go without saving, or the save writes a project and a
        // render for a row that no longer exists.
        if (_editor?.Record?.Id == record.Id)
        {
            _editor.Discard();
            SetScreen(editing: false);
        }

        _store.Delete(record.Id);
        _model.Remove(record.Id);

        var timer = new DispatcherTimer { Interval = UndoWindow };
        var entry = new PendingDelete(record, timer);
        timer.Tick += (_, _) => CommitDelete(entry);

        _pendingDeletes.Add(entry);
        timer.Start();

        ShowUndoToast();
        RefreshFooter();
    }

    /// <summary>The undo window has closed. Now, and only now, the files go.</summary>
    private void CommitDelete(PendingDelete entry)
    {
        entry.Timer.Stop();
        _pendingDeletes.Remove(entry);

        // Every file the capture owns, not just its PNG. An edited capture also has
        // a project and a flattened render, and resolving them from the id rather
        // than the row's columns catches one orphaned by a save that crashed
        // between writing and recording it.
        var files = _store.Assets.All(entry.Record);

        // Any of them could be on the clipboard: the original if the tile was
        // copied, the render if an edited capture was.
        foreach (var file in files) DropFileReferenceFromClipboard(file);
        foreach (var file in files) TryDeleteFile(file);

        ShowUndoToast();
    }

    private void UndoLastDelete()
    {
        if (_pendingDeletes.Count == 0) return;

        var entry = _pendingDeletes[^1];
        entry.Timer.Stop();
        _pendingDeletes.RemoveAt(_pendingDeletes.Count - 1);

        // The file was never touched, so re-inserting the row is a complete undo.
        _store.Insert(entry.Record);
        _model.Restore(entry.Record);

        ShowUndoToast();
        RefreshFooter();
    }

    /// <summary>
    /// Runs every outstanding deletion now. Called when the window hides and again
    /// on exit: a pending timer that never fires would leave the row gone and the
    /// file behind forever.
    /// </summary>
    public void FlushPendingDeletes()
    {
        foreach (var entry in _pendingDeletes.ToArray()) CommitDelete(entry);
    }

    /// <summary>
    /// If the clipboard is currently advertising the file we are about to delete,
    /// republish the same capture as pixels only.
    ///
    /// Copying a capture publishes both the image and the file (CF_HDROP). Apps
    /// that prefer the file — Paint among them — then try to import a path that no
    /// longer exists and report a file error, even though the pixels are sitting
    /// right there in the same clipboard. Spec 2a §6.3 assumed the clipboard held
    /// only bytes and that deletion could not affect a pending paste; publishing
    /// the file made that untrue.
    ///
    /// Deliberately narrow: it only acts when the clipboard still names this exact
    /// path, so a clipboard the user has since filled with something else is left
    /// alone.
    /// </summary>
    private static void DropFileReferenceFromClipboard(string path)
    {
        try
        {
            if (!File.Exists(path)) return;
            if (!System.Windows.Forms.Clipboard.ContainsFileDropList()) return;

            var advertised = System.Windows.Forms.Clipboard.GetFileDropList()
                .Cast<string>()
                .Any(f => string.Equals(f, path, StringComparison.OrdinalIgnoreCase));
            if (!advertised) return;

            // Synchronous, and knowingly so: the file must not be deleted until
            // this has replaced the reference to it. It is one capture on a path
            // the user reaches only by deleting what they just copied.
            ClipboardWriter.Write(PngDecoder.Decode(path), filePath: null);
        }
        catch (Exception e) when (e is ExternalException or ClipboardUnavailableException
                                    or ImageDecodeException or IOException)
        {
            // Losing the clipboard is not worth failing a delete over.
        }
    }

    private static void TryDeleteFile(string path)
    {
        // Best-effort by design: the row is already gone, so a file that refuses
        // to delete is a stale file, not a failure worth interrupting the user for.
        try { File.Delete(path); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    private void ShowUndoToast()
    {
        if (_pendingDeletes.Count == 0)
        {
            UndoToast.Visibility = Visibility.Collapsed;
            return;
        }

        UndoText.Text = _pendingDeletes.Count == 1
            ? "Capture deleted"
            : $"{_pendingDeletes.Count} captures deleted";
        UndoToast.Visibility = Visibility.Visible;
    }

    private void OnGridClick(object sender, MouseButtonEventArgs e)
    {
        var tile = FindAncestor<CaptureTile>(e.OriginalSource as DependencyObject);
        if (tile?.DataContext is not CaptureTileViewModel model) return;

        // A capture with no file has nothing to preview; its only action is to
        // remove the row, which the tile itself offers.
        if (model.IsMissing) return;

        RootContent.Visibility = Visibility.Collapsed;
        _preview.Open(model.Record);
        _preview.Focus();
    }

    /// <summary>
    /// The row for a capture whose file the user deleted behind the database's
    /// back. There is nothing to put in the undo bin, so this is not a delete —
    /// the row simply goes.
    /// </summary>
    private void OnRemoveRequested(CaptureRecord record)
    {
        _store.Delete(record.Id);
        _thumbnails.Remove(record.Id);
        _model.Remove(record.Id);
        UpdateEmptyState();
        RefreshFooter();
    }

    private static T? FindAncestor<T>(DependencyObject? node) where T : DependencyObject
    {
        while (node is not null)
        {
            if (node is T match) return match;
            node = VisualTreeHelper.GetParent(node);
        }
        return null;
    }

    /// <summary>
    /// Pixels moved per wheel notch. WPF's own step is tuned for text lines and
    /// covers most of a tile row here, which reads as the grid lurching rather
    /// than scrolling. Roughly half a row feels like a scroll.
    /// </summary>
    private const double WheelStep = 110;

    protected override void OnPreviewMouseWheel(MouseWheelEventArgs e)
    {
        if (_scroller is null)
        {
            base.OnPreviewMouseWheel(e);
            return;
        }

        _scroller.ScrollToVerticalOffset(
            _scroller.VerticalOffset - e.Delta / 120.0 * WheelStep);
        e.Handled = true;
    }

    private void ApplyColumns()
    {
        var available = RowsHost.ActualWidth > 0 ? RowsHost.ActualWidth : ActualWidth - 56;
        _model.SetColumns((int)Math.Floor((available + 16) / TileStride));
    }

    /// <summary>
    /// Fetches the next page as the end comes into view. The view model guards
    /// re-entrancy — a drag of the scrollbar raises this continuously.
    /// </summary>
    private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (e.ViewportHeight <= 0) return;
        var remaining = e.ExtentHeight - (e.VerticalOffset + e.ViewportHeight);
        if (remaining <= e.ViewportHeight) _model.LoadNextPage();

    }

    /// <summary>Shows or brings forward the single instance.</summary>
    /// <param name="activate">
    /// False when restoring after a capture: the window comes back where it was,
    /// but whatever the user was actually working in keeps keyboard focus. Taking
    /// focus is right when they asked for the library and wrong when they only
    /// asked for a screenshot.
    /// </param>
    public void Reveal(bool activate = true)
    {
        Show();
        if (WindowState == WindowState.Minimized) WindowState = WindowState.Normal;
        if (activate) Activate();
        RefreshFooter();
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        // The editor owns the keyboard while it is on screen, and says so by
        // returning false for keys it does not want. One place decides, so a
        // shortcut can never mean two things at once.
        if (IsEditing && _editor is not null)
        {
            if (_editor.HandleKey(e)) { e.Handled = true; return; }
            base.OnKeyDown(e);
            return;
        }

        if (e.Key == Key.Escape)
        {
            // Esc backs out one level: preview first, then the window.
            if (_preview.IsOpen) _preview.Close();
            else { FlushPendingDeletes(); Hide(); }
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control && _preview.IsOpen)
        {
            _preview.CopyToClipboard();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Set by <see cref="App.OnExit"/> so the process can actually tear the window
    /// down. Without it <see cref="OnClosing"/> refuses every close, including the
    /// one shutdown issues.
    /// </summary>
    public bool AllowClose { get; set; }

    // Hide rather than destroy. A closed-and-disposed window would have to rebuild
    // its grid, re-query, and re-fetch every thumbnail on the next open.
    protected override void OnClosing(CancelEventArgs e)
    {
        // Both paths, before anything else. Dismissing the window while the Edit
        // screen is up used to drop the annotations on the floor: the normal save
        // runs on a background thread, and on shutdown the process exits out from
        // under it. "There is no unsaved state" has to hold here too, or it is
        // just a slogan.
        if (IsEditing) _editor?.SaveNow();

        if (AllowClose)
        {
            FlushPendingDeletes();
            return;
        }

        // Dismissing the window ends the undo window: the toast is gone, so
        // leaving the files alive would strand them with no row referencing them.
        FlushPendingDeletes();
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Count is a single indexed aggregate and stays on the UI thread by the rule
    /// in spec 2a §4.5. The byte total is a directory walk, so it does not.
    /// </summary>
    private void UpdateEmptyState()
    {
        var empty = _model.Count == 0;
        EmptyState.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        EmptyState.Text = _model.IsSearching
            ? $"Nothing matches “{SearchBox.Text.Trim()}”."
            : "No captures yet.\nPress Ctrl+Shift+1 to capture a region.";
    }

    private async void RefreshFooter()
    {
        var count = _store.Count();
        UpdateEmptyState();

        var bytes = await Task.Run(_store.TotalBytes);
        Footer.Text = count == 0
            ? string.Empty
            : $"{count:N0} {(count == 1 ? "capture" : "captures")} · {Describe(bytes)}";
    }

    private static string Describe(long bytes) => bytes switch
    {
        < 1024 => $"{bytes} B",
        < 1024 * 1024 => $"{bytes / 1024.0:N0} KB",
        < 1024L * 1024 * 1024 => $"{bytes / (1024.0 * 1024):N1} MB",
        _ => $"{bytes / (1024.0 * 1024 * 1024):N2} GB",
    };
}
