using System.Diagnostics;
using Microsoft.Data.Sqlite;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Clipboard;
using Snipwhiz.Core.Geometry;
using Snipwhiz.Core.Storage;
using Windows.Win32;

namespace Snipwhiz.Core;

public sealed record CaptureOutcome(
    CaptureRecord? Record,
    bool ClipboardOk,
    bool SaveOk,
    bool HasUncoveredPixels,
    string? Warning);

/// <summary>Describes the window that was focused at grab time. Capture-time-only data.</summary>
public static class ForegroundWindow
{
    public static (string App, string Title) Describe()
    {
        var hwnd = PInvoke.GetForegroundWindow();
        if (hwnd.IsNull) return ("", "");

        Span<char> buffer = stackalloc char[512];
        var length = PInvoke.GetWindowText(hwnd, buffer);
        var title = length > 0 ? new string(buffer[..length]) : "";

        var app = "";
        PInvoke.GetWindowThreadProcessId(hwnd, out var pid);
        if (pid != 0)
        {
            try { app = Process.GetProcessById((int)pid).ProcessName; }
            catch (ArgumentException) { /* exited between the two calls */ }
            catch (InvalidOperationException) { }
        }

        return (app, title);
    }
}

public sealed class CapturePipeline(CaptureStore store, Action<CroppedImage>? writeClipboard = null)
{
    // Seam, not an abstraction: ClipboardWriter is static and touches
    // process-global Win32 state, so the ordering rule below is otherwise
    // unprovable except by hand.
    private readonly Action<CroppedImage> _writeClipboard = writeClipboard ?? ClipboardWriter.Write;

    public CaptureOutcome Complete(FrozenDesktop frozen, PixelRect region, string sourceApp, string sourceTitle)
    {
        var image = frozen.Crop(region);

        string? warning = null;
        if (image.HasUncoveredPixels)
            warning = "Part of that selection is not covered by any display; those pixels are blank.";
        else if (IsEntirelyBlack(image))
            warning = "The capture came back black. DRM-protected content cannot be captured by any "
                    + "screenshot tool; fullscreen games must be switched to windowed mode.";

        // Clipboard first — it must not block on disk I/O.
        var clipboardOk = true;
        try
        {
            _writeClipboard(image);
        }
        catch (ClipboardUnavailableException e)
        {
            clipboardOk = false;
            warning ??= $"Could not copy to the clipboard: {e.Message}";
        }

        CaptureRecord? record = null;
        var saveOk = true;
        try
        {
            record = store.Save(image, sourceApp, sourceTitle);
        }
        // SqliteException belongs here too: Save writes the PNG and then inserts a
        // row, and a full disk or a locked DB fails at the insert. Without it the
        // user got a generic failure instead of the disk-write message this row of
        // §6's table promises.
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or SqliteException)
        {
            saveOk = false;
            warning ??= $"Copied to the clipboard, but saving to disk failed: {e.Message}";
        }

        return new CaptureOutcome(record, clipboardOk, saveOk, image.HasUncoveredPixels, warning);
    }

    private static bool IsEntirelyBlack(CroppedImage image)
    {
        for (long i = 0; i < image.Bgra.LongLength; i += 4)
            if (image.Bgra[i] != 0 || image.Bgra[i + 1] != 0 || image.Bgra[i + 2] != 0) return false;
        return true;
    }
}
