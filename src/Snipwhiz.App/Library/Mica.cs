using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Dwm;

namespace Snipwhiz.App.Library;

/// <summary>
/// The compositor-drawn backdrop the approved prototype achieved with CSS
/// backdrop-filter. Windows 11 22H2 is the platform floor, so the attribute is
/// always available in principle — but this is still best-effort, because a
/// remote session or a machine with transparency effects switched off will
/// refuse it and there is nothing to do about that except look fine anyway.
/// </summary>
internal static class Mica
{
    /// <summary>
    /// Both attributes, not just the backdrop. Without the dark-mode one the
    /// caption bar stays light over a dark window, which reads as a bug rather
    /// than as a deliberate fallback.
    /// </summary>
    public static bool TryApply(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return false;

        var handle = (HWND)hwnd;
        var dark = 1;
        var backdrop = (int)DWM_SYSTEMBACKDROP_TYPE.DWMSBT_MAINWINDOW;

        unsafe
        {
            var darkOk = PInvoke.DwmSetWindowAttribute(
                handle, DWMWINDOWATTRIBUTE.DWMWA_USE_IMMERSIVE_DARK_MODE,
                &dark, sizeof(int)).Succeeded;

            var backdropOk = PInvoke.DwmSetWindowAttribute(
                handle, DWMWINDOWATTRIBUTE.DWMWA_SYSTEMBACKDROP_TYPE,
                &backdrop, sizeof(int)).Succeeded;

            return darkOk && backdropOk;
        }
    }

    /// <summary>
    /// Turns off the open/close animation for this window.
    ///
    /// The library hides itself before a capture, but Windows fades it out rather
    /// than removing it, so the grab caught it mid-fade and the screenshot
    /// contained a translucent library. With transitions off, Hide is immediate
    /// and there is no fade to race.
    /// </summary>
    public static void DisableTransitions(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        var disable = 1;
        unsafe
        {
            PInvoke.DwmSetWindowAttribute(
                (HWND)hwnd, DWMWINDOWATTRIBUTE.DWMWA_TRANSITIONS_FORCEDISABLED,
                &disable, sizeof(int));
        }
    }

    /// <summary>
    /// Blocks until the compositor has presented the current frame.
    ///
    /// Hiding a window only queues the work; the dispatcher reaching Render says
    /// nothing about what DWM has actually composited to the screen, and the
    /// capture reads the screen. This is the one call that answers the question
    /// the capture is really asking.
    /// </summary>
    public static void WaitForCompositor()
    {
        try { PInvoke.DwmFlush(); }
        catch (Exception e) when (e is EntryPointNotFoundException or DllNotFoundException)
        {
            // Composition disabled — nothing to wait for.
        }
    }
}
