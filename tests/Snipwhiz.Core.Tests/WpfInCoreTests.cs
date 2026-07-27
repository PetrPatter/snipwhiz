using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Xunit;

namespace Snipwhiz.Core.Tests;

/// <summary>
/// Proves the one thing spec 2b §1 bets on: that <c>Snipwhiz.Core</c> can drive
/// the WPF render stack, so <c>Annotation.Render(DrawingContext)</c> can live
/// there and be called by both the on-screen canvas and the flattener.
///
/// If this cannot be made to pass, the fallback is moving <c>Render</c> into
/// <c>Snipwhiz.App</c> at the cost of a type switch over every annotation type —
/// which is why it is task 1 and not task 6.
/// </summary>
public class WpfInCoreTests
{
    [Fact]
    public void A_drawing_visual_renders_to_a_bitmap_on_an_sta_thread()
    {
        var pixels = Sta.Run(() =>
        {
            var visual = new DrawingVisual();
            using (var dc = visual.RenderOpen())
                dc.DrawRectangle(Brushes.Red, null, new Rect(0, 0, 4, 4));

            var target = new RenderTargetBitmap(4, 4, 96, 96, PixelFormats.Pbgra32);
            target.Render(visual);

            var buffer = new byte[4 * 4 * 4];
            target.CopyPixels(buffer, stride: 4 * 4, offset: 0);
            return buffer;
        });

        // Pbgra32 is B, G, R, A. An empty render leaves this all zero, so the
        // red channel is what distinguishes "drew" from "did nothing".
        Assert.Equal(0, pixels[0]);
        Assert.Equal(0, pixels[1]);
        Assert.Equal(255, pixels[2]);
        Assert.Equal(255, pixels[3]);
    }
}
