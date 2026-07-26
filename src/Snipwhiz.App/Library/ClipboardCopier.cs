using Snipwhiz.Core.Clipboard;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App.Library;

public enum CopyResult
{
    Copied,
    ClipboardUnavailable,
    FileUnreadable,
}

/// <summary>
/// The one and only path from a stored capture to the clipboard.
///
/// Both the preview's Copy button and its Ctrl+C handler come through here. Two
/// call sites reaching <see cref="ClipboardWriter"/> separately is how one of
/// them eventually stops matching the other — and the thing that would silently
/// diverge is the multi-format payload that stops pastes coming out with black
/// or blue backgrounds.
/// </summary>
internal static class ClipboardCopier
{
    public static Task<CopyResult> CopyAsync(CaptureStore store, CaptureRecord record)
    {
        var path = store.ResolvePath(record);

        // Never on the UI thread: this decodes a full PNG, re-encodes it to PNG
        // inside the writer, and can spend up to eight 60 ms sleeps waiting for a
        // clipboard manager to let go. On a 4K capture that is a visible freeze.
        return Task.Run(() =>
        {
            try
            {
                ClipboardWriter.Write(PngDecoder.Decode(path), path);
                return CopyResult.Copied;
            }
            catch (ClipboardUnavailableException)
            {
                return CopyResult.ClipboardUnavailable;
            }
            catch (ImageDecodeException)
            {
                return CopyResult.FileUnreadable;
            }
        });
    }
}
