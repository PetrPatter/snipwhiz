namespace Snipwhiz.App.Diagnostics;

/// <summary>
/// Negative controls for the tile refresh after an editor save.
///
/// <para>Two of them, because the refresh has <b>two independent</b> failure modes
/// and fixing either one hides the other. A build with only the handler restored
/// still shows a stale tile; a build with only the cache flag restored never
/// re-fetches at all. Proving one and assuming the other is how this ships broken.</para>
///
/// <code>
/// $env:SNIPWHIZ_VERIFY_BREAK_REFRESH = "1"      # the library never hears about the save
/// $env:SNIPWHIZ_VERIFY_BREAK_IMAGECACHE = "1"   # WPF serves the bitmap from before the edit
/// </code>
///
/// <para>The visible symptom of both is identical, and it is the one users report
/// as data loss: "my edits didn't save" for a save that worked perfectly.</para>
/// </summary>
internal static class RefreshVerification
{
    /// <summary>Drops the save notification, so nothing asks the tile to update.</summary>
    public static bool BreakRefresh =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_BREAK_REFRESH") == "1";

    /// <summary>Leaves WPF's imaging cache free to return the pre-edit bitmap.</summary>
    public static bool BreakImageCache =>
        Environment.GetEnvironmentVariable("SNIPWHIZ_VERIFY_BREAK_IMAGECACHE") == "1";
}
