using System.Diagnostics;
using Microsoft.Win32;

namespace Snipwhiz.Core;

public static class PrintScreenTakeover
{
    private const string KeyPath = @"Control Panel\Keyboard";
    private const string ValueName = "PrintScreenKeyForSnippingEnabled";

    /// <summary>
    /// True when Windows routes PrintScreen to Snipping Tool. Absent value means
    /// enabled — Windows 11 ships this on.
    /// </summary>
    public static bool IsSnippingToolBound()
    {
        using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
        var value = key?.GetValue(ValueName);
        return value is not int enabled || enabled != 0;
    }

    /// <summary>Clears the binding. HKCU, so no elevation. Only ever call with explicit consent.</summary>
    public static void Release()
    {
        using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true);
        key?.SetValue(ValueName, 0, RegistryValueKind.DWord);
    }

    /// <summary>
    /// Best effort. There is no API to ask who owns a hotkey, so we can only
    /// name the usual suspects when they are running.
    /// </summary>
    public static string? DescribeLikelyHolder()
    {
        foreach (var (process, label) in new[]
                 {
                     ("Dropbox", "Dropbox"),
                     ("OneDrive", "OneDrive"),
                     ("ShareX", "ShareX"),
                     ("Greenshot", "Greenshot"),
                 })
        {
            if (Process.GetProcessesByName(process).Length > 0) return label;
        }
        return null;
    }
}
