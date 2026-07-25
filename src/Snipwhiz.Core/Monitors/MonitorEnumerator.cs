using System.Runtime.InteropServices;
using Snipwhiz.Core.Geometry;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.HiDpi;

namespace Snipwhiz.Core.Monitors;

public static class MonitorEnumerator
{
    /// <summary>
    /// Physical-pixel bounds per display. Requires the process to be PerMonitorV2
    /// DPI aware, otherwise Windows lies about the bounds.
    /// </summary>
    public static unsafe IReadOnlyList<MonitorInfo> Enumerate()
    {
        var found = new List<MonitorInfo>();

        // CsWin32 generates EnumDisplayMonitors' callback parameter (MONITORENUMPROC) as a
        // managed delegate marshaled to a native function pointer, not a delegate* we could
        // hand it via [UnmanagedCallersOnly]. A GCHandle keeps the delegate rooted for the
        // duration of the call so the GC cannot collect it mid-enumeration.
        MONITORENUMPROC callback = (monitor, _, _, _) => Callback(monitor, found);
        var handle = GCHandle.Alloc(callback);
        try
        {
            PInvoke.EnumDisplayMonitors(default, null, callback, default);
        }
        finally
        {
            handle.Free();
        }

        if (found.Count == 0) throw new InvalidOperationException("No displays were enumerated.");
        return found;
    }

    private static unsafe BOOL Callback(HMONITOR monitor, List<MonitorInfo> list)
    {
        var mi = new MONITORINFOEXW { monitorInfo = { cbSize = (uint)sizeof(MONITORINFOEXW) } };
        if (!PInvoke.GetMonitorInfo(monitor, (MONITORINFO*)&mi)) return true;

        var r = mi.monitorInfo.rcMonitor;

        // MDT_EFFECTIVE_DPI is the scale the user actually chose in Settings.
        PInvoke.GetDpiForMonitor(monitor, MONITOR_DPI_TYPE.MDT_EFFECTIVE_DPI, out var dpiX, out _);

        list.Add(new MonitorInfo(
            DeviceName: mi.szDevice.ToString(),
            Bounds: new PixelRect(r.left, r.top, r.right - r.left, r.bottom - r.top),
            Scale: dpiX / 96.0,
            IsPrimary: (mi.monitorInfo.dwFlags & 1u) != 0));   // MONITORINFOF_PRIMARY

        return true;
    }
}
