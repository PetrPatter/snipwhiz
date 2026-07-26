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
}
