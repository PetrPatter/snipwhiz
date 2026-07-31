#:property TargetFramework=net10.0-windows
#:property UseWPF=true
#:property PublishAot=false
#:property PublishTrimmed=false

// The Snipwhiz icon. Run from the repository root:
//
//     dotnet run assets/icon/generate.cs
//
// Writes src/Snipwhiz.App/Snipwhiz.ico and assets/icon/snipwhiz-512.png.
//
// Generated rather than committed as an opaque blob, because it took five rounds
// to settle. A hex value is a reviewable change; a repainted .ico is not.
//
// Three things were tried and rejected, each by looking at 16px rather than at
// an argument:
//
//   A glass body, with specular highlights and a rim light. It is the current
//   design language and it looked good at 256, but in the tray a dark body reads
//   as a hole punched between nine bright neighbours. The shine that gives depth
//   at 256 is three grey pixels at 16.
//
//   A body at all. The neighbours that look crisp in the tray mostly have none,
//   which is exactly why their glyph looks bigger - it gets all 16 pixels rather
//   than the 60% left after a body and its padding.
//
//   One gradient flowing across the whole mark. More fashionable, and wrong here:
//   a diagonal gradient spends its midrange across all four brackets, so at 16px
//   the pinks and violets swamp the orange and the teal and it reads as a smear.
//
// What is left is flat, bright, large, and four solid colours - one per corner,
// each staying itself at every size.

using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

Color C(string hex) => (Color)ColorConverter.ConvertFromString(hex)!;

// Saturated on purpose. These sit beside a saturated red, a saturated green and
// two blues in the notification area, and restraint reads as dullness there.
// Clockwise from top-left.
var Corners = new[] { C("#FFA12E"), C("#FF3D6E"), C("#A46BFF"), C("#1FE0B4") };

BitmapSource Render(int size)
{
    var u = (double)size;
    var visual = new DrawingVisual();

    using (var dc = visual.RenderOpen())
    {
        // No body, so the mark runs close to the edge. This is where the size
        // comes from - the canvas never changed, only what was left empty.
        var lo = u * 0.105;
        var hi = u - lo;
        var arm = u * 0.255;

        // Heavier below 24px. A 1px line has no room to carry a colour, and the
        // tray is the smallest surface this appears on.
        var stroke = Math.Max(size <= 20 ? 2.0 : 1.0, Math.Round(u * (size <= 20 ? 0.150 : 0.098)));

        var corners = new[]
        {
            (lo, lo,  1.0,  1.0),
            (hi, lo, -1.0,  1.0),
            (hi, hi, -1.0, -1.0),
            (lo, hi,  1.0, -1.0),
        };

        for (var i = 0; i < 4; i++)
        {
            var (x, y, dx, dy) = corners[i];
            var pen = new Pen(new SolidColorBrush(Corners[i]), stroke)
            {
                StartLineCap = PenLineCap.Round,
                EndLineCap = PenLineCap.Round,
            };
            dc.DrawLine(pen, new Point(x, y), new Point(x + arm * dx, y));
            dc.DrawLine(pen, new Point(x, y), new Point(x, y + arm * dy));
        }
    }

    var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
    bitmap.Render(visual);
    bitmap.Freeze();
    return bitmap;
}

byte[] Png(BitmapSource source)
{
    var encoder = new PngBitmapEncoder();
    encoder.Frames.Add(BitmapFrame.Create(source));
    using var stream = new MemoryStream();
    encoder.Save(stream);
    return stream.ToArray();
}

// 16 and 20 for the tray and title bar, 24 and 32 for Explorer and Alt+Tab, 40
// through 64 for large icons, 128 and 256 for the installer.
int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
var frames = sizes.Select(s => Png(Render(s))).ToArray();

using var ico = new MemoryStream();
using (var w = new BinaryWriter(ico, System.Text.Encoding.UTF8, leaveOpen: true))
{
    w.Write((short)0);                  // reserved
    w.Write((short)1);                  // type: icon
    w.Write((short)sizes.Length);

    var offset = 6 + 16 * sizes.Length;
    for (var i = 0; i < sizes.Length; i++)
    {
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));   // 0 means 256
        w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
        w.Write((byte)0);               // palette size
        w.Write((byte)0);               // reserved
        w.Write((short)1);              // colour planes
        w.Write((short)32);             // bits per pixel
        w.Write(frames[i].Length);
        w.Write(offset);
        offset += frames[i].Length;
    }

    foreach (var frame in frames) w.Write(frame);
}

var icoPath = Path.Combine("src", "Snipwhiz.App", "Snipwhiz.ico");
if (!Directory.Exists(Path.GetDirectoryName(icoPath)))
    throw new DirectoryNotFoundException("Run this from the repository root: dotnet run assets/icon/generate.cs");

File.WriteAllBytes(icoPath, ico.ToArray());
Console.WriteLine($"{icoPath}  -  {sizes.Length} sizes, {ico.Length:N0} bytes");

var pngPath = Path.Combine("assets", "icon", "snipwhiz-512.png");
File.WriteAllBytes(pngPath, Png(Render(512)));
Console.WriteLine($"{pngPath}  -  for the README and anywhere an .ico will not go");
