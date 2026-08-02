using System.Windows;
using System.Windows.Interop;

namespace Snipwhiz.App.Library;

/// <summary>
/// Gives an app-drawn maximise button the two things Windows grants only to its own:
/// the <b>Snap Layouts</b> flyout when the pointer rests on it, and the hover
/// highlight while the pointer is inside it.
///
/// <para><see cref="System.Windows.Shell.WindowChrome"/> hands back drag, double-click
/// to maximise and the right-click system menu for free, as long as the caption has a
/// real height. Snap Layouts is the one it cannot: the shell shows that flyout when a
/// window answers <c>WM_NCHITTEST</c> with <c>HTMAXBUTTON</c>, and a WPF button in the
/// caption answers with <c>HTCAPTION</c> like everything else up there. Windows 11
/// users reach the four-pane layout picker through that flyout, so a custom caption
/// without this quietly removes a feature of the operating system.</para>
///
/// <para><b>The button is deliberately not marked hit-test-visible in chrome.</b>
/// Doing that would give WPF the mouse and take away <c>HTMAXBUTTON</c> — you cannot
/// have both. So once Windows owns the hit-test it owns the whole interaction, and
/// the hover highlight and the click have to be driven from here as well. That is why
/// this class needs a callback to paint hover rather than a style trigger: the button
/// never sees <c>IsMouseOver</c> go true.</para>
/// </summary>
internal sealed class CaptionChrome
{
    private const int WM_NCHITTEST = 0x0084;
    private const int WM_NCMOUSELEAVE = 0x02A2;
    private const int WM_NCLBUTTONDOWN = 0x00A1;
    private const int WM_NCLBUTTONUP = 0x00A2;

    private const int HTMAXBUTTON = 9;

    private readonly Window _window;
    private readonly FrameworkElement _maximise;
    private readonly Action<bool> _hover;

    private bool _hovered;

    private CaptionChrome(Window window, FrameworkElement maximise, Action<bool> hover)
    {
        _window = window;
        _maximise = maximise;
        _hover = hover;
    }

    /// <summary>
    /// Hooks the window's message loop. Call from <c>SourceInitialized</c> or later —
    /// there is no <see cref="HwndSource"/> to hook before that.
    /// </summary>
    public static void Install(Window window, FrameworkElement maximise, Action<bool> hover)
    {
        if (PresentationSource.FromVisual(window) is not HwndSource source) return;

        var chrome = new CaptionChrome(window, maximise, hover);
        source.AddHook(chrome.OnMessage);
    }

    private IntPtr OnMessage(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        switch (msg)
        {
            case WM_NCHITTEST:
                // Fires continuously as the pointer crosses the caption, so the miss
                // case is also what turns the highlight back off.
                if (!Over(lParam))
                {
                    SetHover(false);
                    return IntPtr.Zero;
                }

                SetHover(true);
                handled = true;
                return HTMAXBUTTON;

            case WM_NCMOUSELEAVE:
                SetHover(false);
                break;

            // Swallowed. Left to Windows, a press here begins a caption drag and the
            // window tears off the moment the pointer moves a pixel.
            case WM_NCLBUTTONDOWN when Wparam(wParam) == HTMAXBUTTON:
                handled = true;
                break;

            // Acted on release rather than press, which is what lets a press followed
            // by a drag away from the button cancel, as every other button does.
            case WM_NCLBUTTONUP when Wparam(wParam) == HTMAXBUTTON:
                handled = true;
                Toggle();
                break;
        }

        return IntPtr.Zero;
    }

    private static int Wparam(IntPtr wParam) => (int)(wParam.ToInt64() & 0xFFFF);

    /// <summary>Whether a <c>WM_NCHITTEST</c> point lands on the maximise button.</summary>
    private bool Over(IntPtr lParam)
    {
        // Not merely IsVisible: PointFromScreen throws on a visual with no
        // PresentationSource, and this runs during teardown too.
        if (!_maximise.IsVisible || PresentationSource.FromVisual(_maximise) is null) return false;

        // Screen coordinates, packed as two *signed* shorts — negative on a monitor
        // left of or above the primary. Masking to 32 bits first because the high half
        // of lParam is not ours to read and ToInt32 would throw on it.
        var packed = (uint)(lParam.ToInt64() & 0xFFFFFFFF);
        var screen = new Point((short)(packed & 0xFFFF), (short)(packed >> 16));

        try
        {
            return new Rect(_maximise.RenderSize).Contains(_maximise.PointFromScreen(screen));
        }
        catch (InvalidOperationException)
        {
            // The visual left the tree between the guard above and here.
            return false;
        }
    }

    private void SetHover(bool hovered)
    {
        if (hovered == _hovered) return;
        _hovered = hovered;
        _hover(hovered);
    }

    private void Toggle()
    {
        if (_window.WindowState == WindowState.Maximized) SystemCommands.RestoreWindow(_window);
        else SystemCommands.MaximizeWindow(_window);
    }
}
