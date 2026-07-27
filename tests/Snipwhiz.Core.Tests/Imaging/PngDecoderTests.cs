using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Storage;
using Xunit;

namespace Snipwhiz.Core.Tests.Imaging;

public class PngDecoderTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "snipwhiz-tests", Guid.NewGuid().ToString("N"));

    public PngDecoderTests() => Directory.CreateDirectory(_dir);

    /// <summary>
    /// Every channel carries a different function of the coordinates, and none of
    /// them can be confused with another: blue varies with x, green varies with y
    /// but is offset so it never equals blue, red is constant and unlike both.
    ///
    /// Spec 1's loupe fixture encoded red as x and green as y and then only
    /// sampled points where x == y, which made it blind to precisely the red/green
    /// swap it existed to catch. A fixture whose channels can be mistaken for one
    /// another proves nothing about channel order.
    /// </summary>
    private static CroppedImage Fixture(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var o = (y * width + x) * 4;
            bgra[o + 0] = (byte)(x * 10);        // B
            bgra[o + 1] = (byte)(y * 10 + 5);    // G — offset so it never equals B
            bgra[o + 2] = 200;                   // R — constant, unlike either
            bgra[o + 3] = 255;                   // A — opaque
        }
        return new CroppedImage(bgra, width, height, false);
    }

    private string WriteFixture(int width, int height)
    {
        var image = Fixture(width, height);
        var path = Path.Combine(_dir, $"{width}x{height}.png");
        File.WriteAllBytes(path, PngEncoder.Encode(image.Bgra, image.Width, image.Height));
        return path;
    }

    [Fact]
    public void Round_trip_preserves_every_byte()
    {
        var path = WriteFixture(4, 3);
        var expected = Fixture(4, 3);

        var decoded = PngDecoder.Decode(path);

        Assert.Equal(expected.Bgra, decoded.Bgra);
    }

    [Fact]
    public void Dimensions_are_not_transposed()
    {
        var path = WriteFixture(4, 3);

        var decoded = PngDecoder.Decode(path);

        Assert.Equal(4, decoded.Width);
        Assert.Equal(3, decoded.Height);
    }

    [Fact]
    public void Channel_order_survives_the_round_trip()
    {
        // Asserted at a point where x != y, so a red/green or blue/green swap
        // produces different values rather than coincidentally equal ones.
        var path = WriteFixture(4, 3);

        var decoded = PngDecoder.Decode(path);

        var o = (2 * 4 + 1) * 4;   // x = 1, y = 2
        Assert.Equal(10, decoded.Bgra[o + 0]);    // B = x * 10
        Assert.Equal(25, decoded.Bgra[o + 1]);    // G = y * 10 + 5
        Assert.Equal(200, decoded.Bgra[o + 2]);   // R
        Assert.Equal(255, decoded.Bgra[o + 3]);   // A
    }

    [Fact]
    public void A_file_that_is_not_an_image_throws_a_typed_exception()
    {
        var path = Path.Combine(_dir, "garbage.png");
        File.WriteAllBytes(path, [0x00, 0x01, 0x02, 0x03]);

        Assert.Throws<ImageDecodeException>(() => PngDecoder.Decode(path));
    }

    [Fact]
    public void A_missing_file_throws_a_typed_exception()
    {
        var path = Path.Combine(_dir, "does-not-exist.png");

        Assert.Throws<ImageDecodeException>(() => PngDecoder.Decode(path));
    }

    [Fact]
    public void ResolvePath_names_the_file_that_Save_wrote()
    {
        using var store = new CaptureStore(_dir);
        var record = store.Save(Fixture(8, 4), "app", "title");

        var resolved = store.ResolvePath(record);

        Assert.True(Path.IsPathFullyQualified(resolved));
        Assert.True(File.Exists(resolved));
    }

    [Fact]
    public void A_saved_capture_decodes_back_to_the_pixels_it_was_saved_from()
    {
        // The end-to-end path the library actually uses: Save, then resolve, then
        // decode. Exercises the relative-to-absolute join, not just the decoder.
        using var store = new CaptureStore(_dir);
        var original = Fixture(6, 5);
        var record = store.Save(original, "app", "title");

        var decoded = PngDecoder.Decode(store.ResolvePath(record));

        Assert.Equal(original.Bgra, decoded.Bgra);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }
}
