using Microsoft.Win32;

namespace Snipwhiz.App;

/// <summary>
/// The one place in this app that touches the registry.
///
/// <para>Extracted from <see cref="TrayHost"/> when uninstall needed to remove the
/// same value the tray writes. Two copies of a registry path is how an uninstaller
/// ends up leaving a Run entry pointing at a deleted exe — Windows then fails to
/// start it silently at every login, forever, and nothing on the machine explains
/// why.</para>
///
/// <para>HKCU, never HKLM: this needs no elevation and affects only the person who
/// asked for it.</para>
/// </summary>
internal static class AutostartEntry
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValue = "Snipwhiz";

    public static void Set(bool enabled)
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
        if (key is null) return;

        if (enabled) key.SetValue(RunValue, $"\"{Environment.ProcessPath}\"");
        else key.DeleteValue(RunValue, throwOnMissingValue: false);
    }

    /// <summary>Whether Windows currently has an entry for this app.</summary>
    public static bool Exists()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key?.GetValue(RunValue) is not null;
    }
}
