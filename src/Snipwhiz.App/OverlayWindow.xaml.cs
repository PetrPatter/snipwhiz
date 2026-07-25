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

    public MonitorInfo Monitor => _monitor;
    public Canvas Overlay => Layer;

    public event Action? Cancelled;

    public OverlayWindow(FrozenDesktop frozen, MonitorInfo monitor)
    {
        InitializeComponent();
        _frozen = frozen;
        _monitor = monitor;

        // Esc and right-click cancel on EVERY overlay, not just the focused one —
        // otherwise the unfocused screens are dead ends.
        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) Cancelled?.Invoke(); };
        MouseRightButtonUp += (_, _) => Cancelled?.Invoke();

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
    /// the monitor's physical bounds exactly, and the frozen slice is (re-)rendered
    /// against that confirmed-correct state. This is the diagnostic that found the
    /// original DPI-virtualization bug; it stays live rather than being a one-off
    /// debugging aid, because a window that is the wrong physical size — or whose
    /// render transform disagrees with its physical size — renders a mis-scaled 1:1
    /// image and maps drags to the wrong saved pixels, silently otherwise.
    /// </summary>
    private void EnsureCorrectRendering()
    {
        var hwnd = new WindowInteropHelper(this).Handle;
        if (!PhysicalBoundsMatch(hwnd))
        {
            ApplyPhysicalBounds(hwnd);
            if (!PhysicalBoundsMatch(hwnd))
            {
                // Still wrong after a direct reapply: showing a mis-scaled opaque
                // overlay would silently corrupt whatever the user selects. Abort
                // this capture rather than show it.
                Cancelled?.Invoke();
                return;
            }
        }
        RenderFrozenSlice();
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
