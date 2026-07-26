using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
    private readonly LibraryViewModel _model;

    public LibraryWindow(CaptureStore store, ThumbnailCache thumbnails)
    {
        _store = store;
        InitializeComponent();

        _model = new LibraryViewModel(store, thumbnails);
        DataContext = _model;

        SourceInitialized += (_, _) =>
        {
            // The XAML sets Background to Transparent so the backdrop can show
            // through. If the compositor refuses it that would leave a see-through
            // window, so the flat fallback is applied here.
            if (!Mica.TryApply(new WindowInteropHelper(this).Handle))
                Background = (System.Windows.Media.Brush)FindResource("Surface");
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

    private ScrollViewer? _scroller;

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
            Hide();
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
        if (AllowClose) return;
        e.Cancel = true;
        Hide();
    }

    /// <summary>
    /// Count is a single indexed aggregate and stays on the UI thread by the rule
    /// in spec 2a §4.5. The byte total is a directory walk, so it does not.
    /// </summary>
    private async void RefreshFooter()
    {
        var count = _store.Count();
        EmptyState.Visibility = count == 0 ? Visibility.Visible : Visibility.Collapsed;

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
