using Snipwhiz.Core.Geometry;
using Snipwhiz.Core.Monitors;
using Windows.Win32;
using Windows.Win32.Graphics.Gdi;
using Windows.Win32.UI.WindowsAndMessaging;

namespace Snipwhiz.Core.Capture;

public sealed class BitBltGrabber : IDesktopGrabber
{
    // CAPTUREBLT is required: without it the capture silently drops layered
    // content such as menu and tooltip shadows. It costs a brief flicker on
    // some configurations, which we accept — missing content is a correctness
    // bug, flicker is cosmetic.
    private const ROP_CODE Rop = (ROP_CODE)(0x00CC0020 /*SRCCOPY*/ | 0x40000000 /*CAPTUREBLT*/);

    public unsafe FrozenDesktop Grab()
    {
        var desktop = VirtualDesktop.FromMonitors(MonitorEnumerator.Enumerate());
        var b = desktop.Bounds;

        var cursor = ReadCursor();

        HDC screen = default;
        HDC mem = default;
        HBITMAP bmp = default;
        HGDIOBJ previous = default;
        try
        {
            screen = PInvoke.GetDC(default);
            if (screen.IsNull) throw new InvalidOperationException("GetDC(NULL) failed.");

            mem = PInvoke.CreateCompatibleDC(screen);
            if (mem.IsNull) throw new InvalidOperationException("CreateCompatibleDC failed.");

            bmp = PInvoke.CreateCompatibleBitmap(screen, b.Width, b.Height);
            if (bmp.IsNull) throw new InvalidOperationException("CreateCompatibleBitmap failed.");

            previous = PInvoke.SelectObject(mem, bmp);

            if (!PInvoke.BitBlt(mem, 0, 0, b.Width, b.Height, screen, b.X, b.Y, Rop))
                throw new InvalidOperationException("BitBlt failed.");

            var pixels = ReadPixels(mem, bmp, b.Width, b.Height);
            return new FrozenDesktop(desktop, pixels, cursor);
        }
        finally
        {
            // Every handle released on every path — this process runs for weeks.
            if (!previous.IsNull) PInvoke.SelectObject(mem, previous);
            if (!bmp.IsNull) PInvoke.DeleteObject(bmp);
            if (!mem.IsNull) PInvoke.DeleteDC(mem);
            if (!screen.IsNull) PInvoke.ReleaseDC(default, screen);
        }
    }

    private static unsafe byte[] ReadPixels(HDC dc, HBITMAP bmp, int width, int height)
    {
        var info = new BITMAPINFO
        {
            bmiHeader = new BITMAPINFOHEADER
            {
                biSize = (uint)sizeof(BITMAPINFOHEADER),
                biWidth = width,
                biHeight = -height,             // negative => top-down
                biPlanes = 1,
                biBitCount = 32,
                biCompression = (uint)BI_COMPRESSION.BI_RGB,
            }
        };

        var buffer = new byte[(long)width * height * 4];
        fixed (byte* p = buffer)
        {
            var scanned = PInvoke.GetDIBits(dc, bmp, 0, (uint)height, p, &info, DIB_USAGE.DIB_RGB_COLORS);
            if (scanned == 0) throw new InvalidOperationException("GetDIBits failed.");
        }

        // BitBlt from the screen leaves the alpha channel undefined. Force opaque
        // so downstream PNG and CF_DIBV5 consumers do not render it transparent.
        for (long i = 3; i < buffer.LongLength; i += 4) buffer[i] = 255;

        return buffer;
    }

    private static unsafe CursorState ReadCursor()
    {
        var ci = new CURSORINFO { cbSize = (uint)sizeof(CURSORINFO) };
        if (!PInvoke.GetCursorInfo(&ci) || ci.flags != CURSORINFO_FLAGS.CURSOR_SHOWING)
            return CursorState.None;

        int hotX = 0, hotY = 0;
        var ii = default(ICONINFO);
        if (PInvoke.GetIconInfo((HICON)ci.hCursor.Value, &ii))
        {
            hotX = (int)ii.xHotspot;
            hotY = (int)ii.yHotspot;
            if (!ii.hbmMask.IsNull) PInvoke.DeleteObject(ii.hbmMask);
            if (!ii.hbmColor.IsNull) PInvoke.DeleteObject(ii.hbmColor);
        }

        return new CursorState(true, ci.ptScreenPos.X, ci.ptScreenPos.Y, hotX, hotY, (nint)ci.hCursor.Value);
    }
}
