using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.UI.WindowsAndMessaging;
using Point = System.Windows.Point;

namespace Snipwhiz.App;

public partial class OverlayWindow : Window
{
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

        // Give WPF a startup-location hint that lands on the TARGET monitor
        // before the HWND is even created. Without this, a window created while
        // hinted at the primary monitor and then moved cross-monitor (in
        // OnSourceInitialized, below) triggers WPF's own Per-Monitor-V2 DPI-change
        // handling, which silently re-scales the window using a stale DPI —
        // clobbering the exact physical size we set. The precise, authoritative
        // placement still happens via SetWindowPos in physical pixels below; this
        // is only a same-monitor hint to sidestep the cross-monitor transition.
        WindowStartupLocation = WindowStartupLocation.Manual;
        Left = monitor.Bounds.X;
        Top = monitor.Bounds.Y;

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

        // WS_EX_TOOLWINDOW keeps the overlay out of Alt+Tab and the taskbar.
        var style = PInvoke.GetWindowLong((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE);
        PInvoke.SetWindowLong((HWND)hwnd, WINDOW_LONG_PTR_INDEX.GWL_EXSTYLE,
            style | (int)WINDOW_EX_STYLE.WS_EX_TOOLWINDOW);

        ApplyPhysicalBounds(hwnd);
    }

    /// <summary>
    /// Positions the overlay in PHYSICAL pixels. WPF's Left/Top are DIPs and
    /// would place this window wrongly on every non-primary or scaled monitor.
    /// </summary>
    private void ApplyPhysicalBounds(nint hwnd)
    {
        // CsWin32 does not generate a named HWND_TOPMOST constant (it's a
        // documented magic value, not a WinMD API), so it's constructed here:
        // HWND_TOPMOST is (HWND)(-1).
        var hwndTopmost = (HWND)(IntPtr)(-1);
        PInvoke.SetWindowPos((HWND)hwnd, hwndTopmost,
            _monitor.Bounds.X, _monitor.Bounds.Y, _monitor.Bounds.Width, _monitor.Bounds.Height,
            SET_WINDOW_POS_FLAGS.SWP_NOACTIVATE);
    }

    private void OnLoaded(object? sender, RoutedEventArgs e) => RenderFrozenSlice();

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
