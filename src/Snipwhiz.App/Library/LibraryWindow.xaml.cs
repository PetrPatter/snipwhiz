using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using Snipwhiz.Core.Imaging;
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

    public LibraryWindow(CaptureStore store, ThumbnailCache thumbnails)
    {
        _store = store;
        _thumbnails = thumbnails;
        InitializeComponent();

        _model = new LibraryViewModel(store, thumbnails);
        DataContext = _model;

        _preview = new PreviewView(store);
        _preview.Dismissed += () => RootContent.Visibility = Visibility.Visible;
        _preview.DeleteRequested += Delete;
        PreviewHost.Content = _preview;

        UndoButton.Click += (_, _) => UndoLastDelete();

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
        Closed += (_, _) => CaptureTile.RemoveRequested -= OnRemoveRequested;

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

            ApplyColumns();
            _model.Reload();
            RefreshFooter();
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
    private void Delete(CaptureRecord record)
    {
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

        TryDeleteFile(_store.ResolvePath(entry.Record));
        _thumbnails.Remove(entry.Record.Id);

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

        Diagnostics.GridVerification.Sample(RowsHost, _model.Count);
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
