using System.Drawing;
using System.Windows.Forms;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Clipboard;
using Snipwhiz.Core.Imaging;
using Xunit;

namespace Snipwhiz.Core.Tests.Clipboard;

/// <summary>
/// Asserts what is actually published to the clipboard.
///
/// The obvious check — paste into Word and look — cannot fail here. Every capture
/// Snipwhiz produces is opaque, and the defect the multi-format payload prevents
/// only shows with alpha, so a naive <c>Clipboard.SetImage</c> pastes a
/// perfectly fine-looking screenshot into all four test apps. That check would
/// have passed with the payload gutted.
///
/// So these enumerate instead, and the second test is the control: it performs
/// the naive write and asserts the formats are absent, proving the first test
/// discriminates rather than merely passing.
///
/// The clipboard is process-global and these tests overwrite it. That is the cost
/// of testing the real thing rather than a mock of it.
/// </summary>
[Collection("Clipboard")]
public class ClipboardFormatTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snipwhiz-tests", Guid.NewGuid().ToString("N"));

    public ClipboardFormatTests() => Directory.CreateDirectory(_dir);

    private static CroppedImage Image(int w = 16, int h = 8)
    {
        var bgra = new byte[w * h * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 30;
            bgra[i + 1] = 90;
            bgra[i + 2] = 200;
            bgra[i + 3] = 255;
        }
        return new CroppedImage(bgra, w, h, false);
    }

    /// <summary>
    /// The clipboard is an apartment-threaded API; xUnit runs on the pool, which
    /// is MTA. <see cref="Sta"/> owns the thread; this adds the format read that
    /// has to happen on the same thread as the write.
    /// </summary>
    private static string[] OnStaThread(Action action) => Sta.Run(() =>
    {
        action();
        return System.Windows.Forms.Clipboard.GetDataObject()?.GetFormats(autoConvert: false) ?? [];
    });

    [Fact]
    public void A_capture_is_published_as_png_dibv5_dib_and_a_file()
    {
        var path = Path.Combine(_dir, "capture.png");
        var image = Image();
        File.WriteAllBytes(path, PngEncoder.Encode(image.Bgra, image.Width, image.Height));

        var formats = OnStaThread(() => ClipboardWriter.Write(image, path));

        Assert.Contains("PNG", formats);
        // CF_DIBV5 has no friendly name in the managed enumeration.
        Assert.Contains("Format17", formats);
        Assert.Contains("DeviceIndependentBitmap", formats);
        // The one that lets a terminal, Explorer or a file picker take the
        // capture. Win+Shift+S publishes file formats and no bitmap at all.
        Assert.Contains("FileDrop", formats);
    }

    [Fact]
    public void The_naive_bitmap_write_publishes_none_of_them()
    {
        // The control. Without it the assertions above are just assertions — this
        // is what shows they would actually fail against a broken implementation.
        var formats = OnStaThread(() =>
        {
            using var bitmap = new Bitmap(16, 8);
            System.Windows.Forms.Clipboard.SetImage(bitmap);
        });

        Assert.DoesNotContain("PNG", formats);
        Assert.DoesNotContain("Format17", formats);
        Assert.DoesNotContain("FileDrop", formats);
    }

    [Fact]
    public void A_capture_with_no_file_still_publishes_the_image_formats()
    {
        var formats = OnStaThread(() => ClipboardWriter.Write(Image(), filePath: null));

        Assert.Contains("PNG", formats);
        Assert.Contains("Format17", formats);
        // Nothing to advertise, so nothing is claimed.
        Assert.DoesNotContain("FileDrop", formats);
    }

    [Fact]
    public void A_path_that_does_not_exist_is_not_advertised()
    {
        // Publishing a path to a file that was never written turns a missing
        // feature into a paste that fails inside the consumer.
        var formats = OnStaThread(
            () => ClipboardWriter.Write(Image(), Path.Combine(_dir, "never-written.png")));

        Assert.Contains("PNG", formats);
        Assert.DoesNotContain("FileDrop", formats);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}

/// <summary>Clipboard tests share process-global state, so they never run in parallel.</summary>
[CollectionDefinition("Clipboard", DisableParallelization = true)]
public class ClipboardCollection;
