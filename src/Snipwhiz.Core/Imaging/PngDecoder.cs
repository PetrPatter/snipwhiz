using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using Snipwhiz.Core.Capture;

namespace Snipwhiz.Core.Imaging;

/// <summary>
/// A file could not be read as an image. Distinct from a real I/O fault because
/// callers treat it as "this capture's original is gone or unreadable" and offer
/// to remove the row, rather than reporting a disk problem.
/// </summary>
public sealed class ImageDecodeException(string message, Exception inner)
    : Exception(message, inner);

public static class PngDecoder
{
    /// <summary>
    /// Decodes a saved capture to top-down BGRA32, stride width * 4 — the layout
    /// <see cref="CroppedImage"/> and <c>ClipboardWriter</c> both expect.
    /// </summary>
    /// <remarks>
    /// Alpha comes back <b>premultiplied</b>, because that is what the clipboard's
    /// CF_DIBV5 payload declares it is publishing. Every spec 1 capture is opaque
    /// so the conversion is currently an identity, but the editor in spec 2b
    /// produces transparency and this is where that would otherwise go wrong.
    /// </remarks>
    public static CroppedImage Decode(string path)
    {
        try
        {
            using var source = new Bitmap(path);
            var rect = new Rectangle(0, 0, source.Width, source.Height);

            // Clone into a known format rather than trusting the file's: PNGs can
            // be palettised, 24bpp, or 16-bit per channel, and LockBits does not
            // convert. GDI+ does the conversion here, once.
            using var normalized = source.Clone(rect, PixelFormat.Format32bppPArgb);

            var data = normalized.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppPArgb);
            try
            {
                var bgra = new byte[(long)source.Width * source.Height * 4];
                var rowBytes = source.Width * 4;

                // Row by row: GDI+ pads each row to a 4-byte boundary and our
                // buffer is unpadded. For 32bpp the padding is always zero, but
                // copying by row costs one line and removes the trap.
                for (var y = 0; y < source.Height; y++)
                    Marshal.Copy(data.Scan0 + y * data.Stride, bgra, y * rowBytes, rowBytes);

                return new CroppedImage(bgra, source.Width, source.Height, false);
            }
            finally
            {
                normalized.UnlockBits(data);
            }
        }
        // GDI+ reports a corrupt or non-image file as OutOfMemoryException. That is
        // not a real allocation failure and must not be allowed to look like one.
        catch (Exception e) when (e is ArgumentException or OutOfMemoryException
                                    or FileNotFoundException or DirectoryNotFoundException)
        {
            throw new ImageDecodeException($"Could not decode '{path}' as an image.", e);
        }
    }
}
