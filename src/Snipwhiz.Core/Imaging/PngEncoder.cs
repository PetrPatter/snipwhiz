using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace Snipwhiz.Core.Imaging;

public static class PngEncoder
{
    /// <param name="bgra">Top-down BGRA32, stride width * 4.</param>
    public static byte[] Encode(byte[] bgra, int width, int height)
    {
        var expected = (long)width * height * 4;
        if (bgra.LongLength != expected)
            throw new ArgumentException($"Expected {expected} bytes for {width}x{height}, got {bgra.LongLength}.", nameof(bgra));

        var handle = GCHandle.Alloc(bgra, GCHandleType.Pinned);
        try
        {
            using var bitmap = new Bitmap(width, height, width * 4, PixelFormat.Format32bppArgb,
                                          handle.AddrOfPinnedObject());
            using var ms = new MemoryStream();
            bitmap.Save(ms, ImageFormat.Png);
            return ms.ToArray();
        }
        finally
        {
            handle.Free();
        }
    }
}
