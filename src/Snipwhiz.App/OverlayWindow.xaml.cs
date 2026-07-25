using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Point = System.Windows.Point;

namespace Snipwhiz.App;

public partial class OverlayWindow : Window
{
    // A DPI change (e.g. this window moving cross-monitor while WPF still thinks
    // it's on the monitor it was created near, or the user changing display
    // scaling while the overlay is up) can trigger WPF's own Per-Monitor-V2
    // support to silently rescale the window to preserve its DIP size — clobbering
    // the exact physical size SetWindowPos just set. WM_DPICHANGED = 0x02E0.
    private const int WM_DPICHANGED = 0x02E0;

    private readonly FrozenDesktop _frozen;
    private readonly MonitorInfo _monitor;

    private readonly System.Windows.Shapes.Rectangle _dim = new()
    {
        Fill = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0xAD, 0x0C, 0x08, 0x04)),
    };
    private readonly System.Windows.Shapes.Rectangle _border = new()
    {
        Stroke = new SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xB6, 0x27)),
        StrokeThickness = 1,
    };

    public MonitorInfo Monitor => _monitor;
    public Canvas Overlay => Layer;

    public event Action? Cancelled;
    public event Action<int, int>? DragStarted;
    public event Action<int, int>? PointerMoved;
    public event Action? DragEnded;

    public OverlayWindow(FrozenDesktop frozen, MonitorInfo monitor)
    {
        InitializeComponent();
        _frozen = frozen;
        _monitor = monitor;

        // Esc and right-click cancel on EVERY overlay, not just the focused one —
        // otherwise the unfocused screens are dead ends.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Cancelled?.Invoke(); };
        MouseRightButtonUp += (_, _) => Cancelled?.Invoke();

        MouseLeftButtonDown += (_, e) =>
        {
            var (vx, vy) = ToVirtualPixels(e.GetPosition(this));
            DragStarted?.Invoke(vx, vy);
            CaptureMouse();
        };
        MouseMove += (_, e) =>
        {
            var (vx, vy) = ToVirtualPixels(e.GetPosition(this));
            PointerMoved?.Invoke(vx, vy);
        };
        MouseLeftButtonUp += (_, _) =>
        {
            ReleaseMouseCapture();
            DragEnded?.Invoke();
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += OnLoaded;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        var hwnd = new WindowInteropHelper(this).Handle;

        HwndSource.FromHwnd(hwnd)?.AddHook(WndProc);

        // WS_EX_TOOLWINDOW keeps the overlay out of Alt+Tab and the taskbar.
        var style = PInvoke.GetWindowLong((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            style | (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);

        ApplyPhysicalBounds(hwnd);
    }

    /// <summary>
    /// On every DPI change delivered to this window, schedules a deferred re-check
    /// of our physical bounds and re-render. Deliberately does NOT mark the message
    /// handled and does NOT act synchronously: WPF's own Per-Monitor-V2 handling for
    /// this same message is what updates its internal per-window DPI/render-transform
    /// state, and that must be allowed to run — a raw SetWindowPos alone can force
    /// the outer HWND rect correct while leaving WPF still rendering our DIP-sized
    /// content at a stale DPI (right-sized window, wrong-scaled picture inside it).
    /// Deferring to ApplicationIdle lets WPF's own handling finish first, then we
    /// reassert on top of an already-consistent internal state.
    /// </summary>
    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DPICHANGED)
            Dispatcher.BeginInvoke(new Action(EnsureCorrectRendering), DispatcherPriority.ApplicationIdle);
        return IntPtr.Zero;
    }

    /// <summary>
    /// Positions the overlay in PHYSICAL pixels. WPF's Left/Top are DIPs and
    /// would place this window wrongly on every non-primary or scaled monitor.
    /// </summary>
    private void ApplyPhysicalBounds(nint hwnd)
    {
        PInvoke.SetWindowPos((HWND)hwnd, HWND.HWND_TOPMOST,
            _monitor.Bounds.X, _monitor.Bounds.Y, _monitor.Bounds.Width, _monitor.Bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    private bool PhysicalBoundsMatch(nint hwnd)
    {
        PInvoke.GetWindowRect((HWND)hwnd, out var r);
        var b = _monitor.Bounds;
        return r.left == b.X && r.top == b.Y && r.right == b.Right && r.bottom == b.Bottom;
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) =>
        // Deferred to ApplicationIdle so this runs after every other pending
        // layout/DPI-related dispatcher operation has drained — the point at which
        // the window's rect and WPF's internal DPI state are known to have settled.
        Dispatcher.BeginInvoke(new Action(EnsureCorrectRendering), DispatcherPriority.ApplicationIdle);

    /// <summary>
    /// Permanent invariant check: this window's actual on-screen rect must equal
    /// the monitor's physical bounds exactly, AND the rendered image's DIP size
    /// must agree with WPF's own actual DPI for this window — not just with our
    /// own <see cref="MonitorInfo.Scale"/> metadata, which is exactly what can be
    /// wrong (see <see cref="RenderedImageSizeMatches"/>). This is the diagnostic
    /// that found the original DPI-virtualization bug; it stays live rather than
    /// being a one-off debugging aid, because a window that is the wrong physical
    /// size — or whose render transform disagrees with its physical size — renders
    /// a mis-scaled 1:1 image and maps drags to the wrong saved pixels, silently
    /// otherwise. Runs deferred (from <see cref="OnLoaded"/> and <see cref="WndProc"/>),
    /// so it has its own try/catch: by the time this executes, CaptureSession.Start()
    /// has already returned and its try/catch can no longer see an exception here.
    /// </summary>
    private void EnsureCorrectRendering()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            if (!PhysicalBoundsMatch(hwnd))
            {
                ApplyPhysicalBounds(hwnd);
                if (!PhysicalBoundsMatch(hwnd))
                {
                    // Still wrong after a direct reapply: showing a mis-scaled
                    // opaque overlay would silently corrupt whatever the user
                    // selects. Abort this capture rather than show it.
                    Cancelled?.Invoke();
                    return;
                }
            }

            RenderFrozenSlice();
            if (!RenderedImageSizeMatches())
            {
                // One retry, mirroring the rect check above: WPF's DPI belief
                // for this window can still be settling; re-render against its
                // current state and re-check before giving up.
                RenderFrozenSlice();
                if (!RenderedImageSizeMatches())
                {
                    Cancelled?.Invoke();
                    return;
                }
            }
        }
        catch (Exception)
        {
            // Deferred to ApplicationIdle, so this runs after CaptureSession.Start()
            // has returned — its try/catch can no longer see us. Tear the session
            // down here or the user is left with blank opaque overlays. Rethrow so
            // DispatcherUnhandledException still reports it: the user should get
            // the balloon *and* their screen back.
            Cancelled?.Invoke();
            throw;
        }
    }

    /// <summary>
    /// The rect check above cannot see a mis-scaled *image*: the first WM_DPICHANGED
    /// fix attempt produced a window with a correct outer rect but 71.88% wrong
    /// content, because the outer rect and the rendered picture inside it are two
    /// separate pieces of state. Comparing <c>Frozen.Width</c>/<c>Height</c> against
    /// a value computed from our own <see cref="MonitorInfo.Scale"/> would be
    /// circular — <see cref="RenderFrozenSlice"/> used that exact same field to set
    /// them, so they'd always agree even when <c>Scale</c> itself is wrong. Instead
    /// this cross-checks against <see cref="VisualTreeHelper.GetDpi"/>, WPF's own
    /// live, independent belief about this window's actual rendering DPI, which is
    /// exactly what can disagree with our metadata.
    /// </summary>
    private bool RenderedImageSizeMatches()
    {
        var actualDpi = VisualTreeHelper.GetDpi(this);
        var expectedDipW = Dpi.PhysicalToDip(_monitor.Bounds.Width, actualDpi.DpiScaleX);
        var expectedDipH = Dpi.PhysicalToDip(_monitor.Bounds.Height, actualDpi.DpiScaleY);
        const double tolerance = 0.5; // sub-pixel; layout rounding is expected
        return Math.Abs(Frozen.Width - expectedDipW) < tolerance
            && Math.Abs(Frozen.Height - expectedDipH) < tolerance;
    }

    /// <summary>
    /// Renders this monitor's slice of the frozen desktop at exactly 1:1 physical
    /// pixels. Three things must all agree or the image is soft and offset:
    /// the bitmap's DPI metadata, the Image's DIP size, and NearestNeighbor scaling.
    /// </summary>
    private void RenderFrozenSlice()
    {
        var b = _monitor.Bounds;
        var slice = _frozen.Crop(b);

        // Claim 96 DPI so WPF does no DPI-based rescaling of its own...
        var source = BitmapSource.Create(
            slice.Width, slice.Height, 96, 96, PixelFormats.Bgra32, null,
            slice.Bgra, slice.Width * 4);
        source.Freeze();

        Frozen.Source = source;

        // ...then size it in DIPs so physicalPx / scale lands on exact device pixels.
        Frozen.Width = Dpi.PhysicalToDip(slice.Width, _monitor.Scale);
        Frozen.Height = Dpi.PhysicalToDip(slice.Height, _monitor.Scale);

        // Stretch="Fill" is required now that we set an explicit DIP size:
        // Stretch="None" would draw 1 bitmap px per DIP and overflow at >100%.
        Frozen.Stretch = Stretch.Fill;
    }

    /// <summary>Converts a WPF mouse position on this overlay to virtual physical pixels.</summary>
    public (int X, int Y) ToVirtualPixels(Point dipPoint) => (
        _monitor.Bounds.X + Dpi.DipToPhysical(dipPoint.X, _monitor.Scale),
        _monitor.Bounds.Y + Dpi.DipToPhysical(dipPoint.Y, _monitor.Scale));

    /// <summary>Draws this monitor's slice of a selection that may span several monitors.</summary>
    public void RenderSelection(PixelRect? selection)
    {
        if (_dim.Parent is null)
        {
            Layer.Children.Add(_dim);
            Layer.Children.Add(_border);
        }

        // ActualWidth/ActualHeight are DIPs valid only after layout. Re-read them on
        // every call rather than just when _dim is first added: the first
        // RenderSelection can in principle land before this window's first layout
        // pass has produced a non-zero size, which would otherwise wedge _dim's
        // Width/Height at 0 forever (the add-guard above only runs once).
        _dim.Width = ActualWidth;
        _dim.Height = ActualHeight;

        if (selection is not { } sel)
        {
            _dim.Opacity = 1;
            _border.Visibility = Visibility.Collapsed;
            _dim.Clip = null;
            return;
        }

        var local = sel.Intersect(_monitor.Bounds);
        if (local.IsEmpty)
        {
            _dim.Opacity = 1;
            _dim.Clip = null;
            _border.Visibility = Visibility.Collapsed;
            return;
        }

        var x = Dpi.PhysicalToDip(local.X - _monitor.Bounds.X, _monitor.Scale);
        var y = Dpi.PhysicalToDip(local.Y - _monitor.Bounds.Y, _monitor.Scale);
        var w = Dpi.PhysicalToDip(local.Width, _monitor.Scale);
        var h = Dpi.PhysicalToDip(local.Height, _monitor.Scale);

        // Punch the selection out of the dim layer with an even-odd geometry —
        // the kept region stays at full brightness.
        var outer = new RectangleGeometry(new Rect(0, 0, ActualWidth, ActualHeight));
        var inner = new RectangleGeometry(new Rect(x, y, w, h));
        _dim.Clip = new CombinedGeometry(GeometryCombineMode.Exclude, outer, inner);

        Canvas.SetLeft(_border, x);
        Canvas.SetTop(_border, y);
        _border.Width = w;
        _border.Height = h;
        _border.Visibility = Visibility.Visible;
    }

    public void ShowAt(bool activate)
    {
        if (activate) Show();
        else
        {
            ShowActivated = false;
            Show();
        }
    }
}
