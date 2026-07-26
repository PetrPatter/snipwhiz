using System.Runtime.InteropServices;
using System.Text;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Imaging;
using Windows.Win32;
using Windows.Win32.Foundation;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.System.Memory;

namespace Snipwhiz.Core.Clipboard;

public sealed class ClipboardUnavailableException(string message) : Exception(message);

public static class ClipboardWriter
{
    private const uint CF_DIB = 8;
    private const uint CF_DIBV5 = 17;
    private const uint CF_HDROP = 15;
    private const int MaxAttempts = 8;
    private const int RetryDelayMs = 60;

    /// <summary>
    /// Publishes PNG, CF_DIBV5 and CF_DIB in one sequence. All three, because
    /// modern apps prefer PNG, DIBV5 carries alpha, and supplying CF_DIB stops
    /// Windows synthesising a wrong one from the others.
    /// </summary>
    /// <param name="filePath">
    /// When given, the capture is also published as a file (CF_HDROP). Consumers
    /// that take a file rather than a bitmap — terminals, Explorer, file pickers,
    /// "attach" fields — see nothing at all without this. Measured against
    /// Win+Shift+S, which publishes only file formats and no bitmap of its own.
    /// </param>
    public static unsafe void Write(CroppedImage image, string? filePath = null)
    {
        var png = PngEncoder.Encode(image.Bgra, image.Width, image.Height);
        var pngFormat = PInvoke.RegisterClipboardFormat("PNG");
        if (pngFormat == 0) throw new ClipboardUnavailableException("RegisterClipboardFormat(\"PNG\") failed.");

        // Clipboard managers hold the clipboard constantly; retry before giving up.
        var opened = false;
        for (var attempt = 0; attempt < MaxAttempts && !opened; attempt++)
        {
            opened = PInvoke.OpenClipboard(default);
            if (!opened) Thread.Sleep(RetryDelayMs);
        }
        if (!opened)
            throw new ClipboardUnavailableException(
                $"Another application held the clipboard after {MaxAttempts} attempts.");

        try
        {
            PInvoke.EmptyClipboard();
            SetBytes(pngFormat, png);
            SetBytes(CF_DIBV5, BuildDibV5(image));
            SetBytes(CF_DIB, BuildDib(image));

            // Only when the file is genuinely there. Advertising a path that does
            // not exist gives the consumer a paste that fails rather than one that
            // was never offered.
            if (filePath is not null && File.Exists(filePath))
                SetBytes(CF_HDROP, BuildHDrop(filePath));
        }
        finally
        {
            PInvoke.CloseClipboard();
        }
    }

    private static unsafe void SetBytes(uint format, byte[] data)
    {
        // GMEM_MOVEABLE; ownership passes to the clipboard on success.
        var handle = PInvoke.GlobalAlloc(GLOBAL_ALLOC_FLAGS.GMEM_MOVEABLE, (nuint)data.Length);
        if (handle == 0) throw new ClipboardUnavailableException($"GlobalAlloc failed for format {format}.");

        var ok = false;
        try
        {
            var target = PInvoke.GlobalLock((HGLOBAL)handle);
            if (target is null) throw new ClipboardUnavailableException($"GlobalLock failed for format {format}.");
            try
            {
                Marshal.Copy(data, 0, (nint)target, data.Length);
            }
            finally
            {
                PInvoke.GlobalUnlock((HGLOBAL)handle);
            }

            if (PInvoke.SetClipboardData(format, (HANDLE)(IntPtr)handle) == 0)
                throw new ClipboardUnavailableException($"SetClipboardData failed for format {format}.");

            ok = true;
        }
        finally
        {
            if (!ok) PInvoke.GlobalFree((HGLOBAL)handle);
        }
    }

    /// <summary>
    /// A DROPFILES header followed by the path as UTF-16, double-null terminated.
    /// The list is always one file, but the format is a list, so the second null
    /// is what marks its end — omit it and consumers read past the buffer.
    /// </summary>
    private static byte[] BuildHDrop(string path)
    {
        const int headerSize = 20;   // DWORD pFiles + POINT pt + BOOL fNC + BOOL fWide
        var list = path + "\0\0";
        var buffer = new byte[headerSize + Encoding.Unicode.GetByteCount(list)];

        BitConverter.TryWriteBytes(buffer.AsSpan(0), headerSize);   // offset to the list
        BitConverter.TryWriteBytes(buffer.AsSpan(16), 1);           // fWide: the path is UTF-16
        Encoding.Unicode.GetBytes(list, 0, list.Length, buffer, headerSize);

        return buffer;
    }

    private static unsafe byte[] BuildDibV5(CroppedImage image)
    {
        var header = new BITMAPV5HEADER
        {
            bV5Size = (uint)sizeof(BITMAPV5HEADER),
            bV5Width = image.Width,
            bV5Height = -image.Height,             // negative => top-down
            bV5Planes = 1,
            bV5BitCount = 32,
            bV5Compression = (BI_COMPRESSION)3,    // BI_BITFIELDS
            bV5SizeImage = (uint)image.Bgra.Length,
            bV5RedMask   = 0x00FF0000,
            bV5GreenMask = 0x0000FF00,
            bV5BlueMask  = 0x000000FF,
            bV5AlphaMask = 0xFF000000,
            bV5CSType = 0x73524742,                // 'sRGB'
            bV5Intent = 4,                         // LCS_GM_IMAGES
        };

        var buffer = new byte[sizeof(BITMAPV5HEADER) + image.Bgra.Length];
        fixed (byte* p = buffer) *(BITMAPV5HEADER*)p = header;
        // Alpha is premultiplied; spec 1 output is opaque, spec 2's editor is not.
        Buffer.BlockCopy(image.Bgra, 0, buffer, sizeof(BITMAPV5HEADER), image.Bgra.Length);
        return buffer;
    }

    private static unsafe byte[] BuildDib(CroppedImage image)
    {
        var header = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = image.Width,
            biHeight = -image.Height,
            biPlanes = 1,
            biBitCount = 32,
            biCompression = (uint)BI_COMPRESSION.BI_RGB,
            biSizeImage = (uint)image.Bgra.Length,
        };

        var buffer = new byte[sizeof(BITMAPINFOHEADER) + image.Bgra.Length];
        fixed (byte* p = buffer) *(BITMAPINFOHEADER*)p = header;
        Buffer.BlockCopy(image.Bgra, 0, buffer, sizeof(BITMAPINFOHEADER), image.Bgra.Length);
        return buffer;
    }
}
