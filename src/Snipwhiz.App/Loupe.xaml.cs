using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Capture;

namespace Snipwhiz.App;

public partial class Loupe : System.Windows.Controls.UserControl
{
    private const int SampleSpan = 13;     // odd, so there is a true centre pixel

    private readonly FrozenDesktop _frozen;

    public Loupe(FrozenDesktop frozen)
    {
        InitializeComponent();
        _frozen = frozen;
    }

    /// <summary>Position is virtual physical pixels — the same space the crop uses.</summary>
    public void Update(int virtualX, int virtualY)
    {
        var half = SampleSpan / 2;
        var pixels = new byte[SampleSpan * SampleSpan * 4];

        for (var row = 0; row < SampleSpan; row++)
        for (var col = 0; col < SampleSpan; col++)
        {
            var (r, g, b) = _frozen.SampleAt(virtualX - half + col, virtualY - half + row);
            var i = (row * SampleSpan + col) * 4;
            pixels[i + 0] = b;
            pixels[i + 1] = g;
            pixels[i + 2] = r;
            pixels[i + 3] = 255;
        }

        var source = BitmapSource.Create(SampleSpan, SampleSpan, 96, 96,
            PixelFormats.Bgra32, null, pixels, SampleSpan * 4);
        source.Freeze();
        Glass.Source = source;

        var (cr, cg, cb) = _frozen.SampleAt(virtualX, virtualY);
        Swatch.Fill = new SolidColorBrush(System.Windows.Media.Color.FromRgb(cr, cg, cb));
        Hex.Text = $"#{cr:X2}{cg:X2}{cb:X2}";
        Coords.Text = $"{virtualX}, {virtualY}";
    }
}
