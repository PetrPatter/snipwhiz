using Snipwhiz.Core.Imaging;
using Xunit;

namespace Snipwhiz.Core.Tests.Imaging;

public class PngEncoderTests
{
    [Fact]
    public void Encode_produces_a_valid_png_signature()
    {
        var bgra = new byte[4 * 4 * 4];
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;

        var png = PngEncoder.Encode(bgra, 4, 4);

        Assert.Equal(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A }, png[..8]);
    }

    [Fact]
    public void Encode_round_trips_pixel_values()
    {
        // one red pixel, BGRA order
        var bgra = new byte[] { 0, 0, 255, 255 };
        var png = PngEncoder.Encode(bgra, 1, 1);

        using var ms = new MemoryStream(png);
        using var bmp = new System.Drawing.Bitmap(ms);
        var px = bmp.GetPixel(0, 0);

        Assert.Equal(255, px.R);
        Assert.Equal(0, px.G);
        Assert.Equal(0, px.B);
    }

    [Fact]
    public void Encode_rejects_a_buffer_that_does_not_match_the_dimensions()
        => Assert.Throws<ArgumentException>(() => PngEncoder.Encode(new byte[10], 4, 4));
}
