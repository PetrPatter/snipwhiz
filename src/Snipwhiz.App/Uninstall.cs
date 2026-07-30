using System.Diagnostics;
using System.IO;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App;

/// <summary>
/// What leaves, and what does not, when someone uninstalls Snipwhiz.
///
/// <para>Velopack removes the shortcuts and the install directory itself. Two
/// things it cannot know about are handled here: the autostart value this app may
/// have written to <c>HKCU</c>, and the library.</para>
///
/// <para><b>The library stays.</b> Captures, projects and the database are the
/// user's data that happens to live beside the application, not part of it.
/// Deleting somebody's screenshots because they uninstalled an app is not a
/// decision an uninstaller gets to make. So this says where they are instead — by
/// opening the folder, which is both the clearest way to say it and the only way
/// that fits the budget below.</para>
///
/// <para><b>This runs on a 15-second fuse.</b> Velopack invokes it mid-uninstall
/// and calls <c>Environment.Exit</c> the moment it returns; if it has not returned
/// in 15 seconds the process is terminated, and if it throws, Velopack exits -1 and
/// the uninstall fails. That rules out the obvious implementation — a message box
/// saying where the library is would hang the uninstaller behind a dialog nobody is
/// looking at. Hence a spawned Explorer window, which outlives this process, and
/// hence the catch around everything.</para>
/// </summary>
internal static class Uninstall
{
    public static void Run()
    {
        // Ordered by consequence. A stale Run entry pointing at a deleted exe is a
        // permanent, silent login failure, so it goes first and its failure must not
        // stop it happening.
        try
        {
            AutostartEntry.Set(false);
        }
        catch
        {
            // Nothing to report to and nowhere to report it: no UI, no log, and a
            // throw here aborts the uninstall.
        }

        try
        {
            ShowLibrary();
        }
        catch
        {
            // Explorer is a courtesy. It is not worth failing an uninstall over.
        }
    }

    /// <summary>
    /// Opens the library folder, but only if there is something in it. Someone who
    /// captured nothing has nothing to be told about, and an empty folder appearing
    /// during an uninstall is just noise.
    /// </summary>
    private static void ShowLibrary()
    {
        var root = CaptureStore.ResolveRoot();
        var captures = Path.Combine(root, "captures");

        if (!Directory.Exists(captures)) return;
        if (!Directory.EnumerateFiles(captures, "*", SearchOption.AllDirectories).Any()) return;

        Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
    }
}
