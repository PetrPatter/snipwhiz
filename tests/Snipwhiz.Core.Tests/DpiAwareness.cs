using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Snipwhiz.Core.Tests;

/// <summary>
/// The test host has no application manifest, so without this Windows
/// virtualizes coordinates and MonitorEnumerator returns scaled-down bounds
/// with Scale reported as 1.0 — silently wrong data for every geometry test
/// that touches real displays.
/// </summary>
internal static class DpiAwareness
{
    private static readonly nint PerMonitorAwareV2 = -4;

    [ModuleInitializer]
    internal static void Enable()
    {
        // Best effort: fails harmlessly if the host already set an awareness context.
        try { SetProcessDpiAwarenessContext(PerMonitorAwareV2); }
        catch (EntryPointNotFoundException) { }
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetProcessDpiAwarenessContext(nint value);
}
