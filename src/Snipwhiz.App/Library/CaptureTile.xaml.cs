using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Snipwhiz.App.Library;

public partial class CaptureTile : System.Windows.Controls.UserControl
{
    private CancellationTokenSource? _pending;

    public CaptureTile()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => CancelPending();
        CardContent.SizeChanged += (_, e) => ClipToRoundedCorners(e.NewSize);
    }

    /// <summary>
    /// Rounds off the card's content so it stops painting over the border's corner
    /// arcs — the gaps that appear at the top corners of the hover outline.
    ///
    /// Two things make this less obvious than it looks. <c>ClipToBounds</c> clips
    /// to the bounding rectangle, not the rounded shape, so it does nothing here.
    /// And clipping the <c>Border</c> itself does not help either: that only
    /// removes what falls outside the rounded rect, while the problem is the child
    /// painting <i>inside</i> it, on top of the stroke. Border renders its own
    /// background and stroke first and children afterwards, and although the child
    /// is inset by the border thickness, a corner arc curves inward far further
    /// than that — so the child's square corner still covers it.
    ///
    /// The clip therefore belongs on the content, with the radius reduced by the
    /// border thickness so the two curves stay concentric.
    /// </summary>
    private void ClipToRoundedCorners(System.Windows.Size size)
    {
        if (size.Width <= 0 || size.Height <= 0) return;

        var inset = Card.BorderThickness.Left;
        var radius = Math.Max(0, Card.CornerRadius.TopLeft - inset);

        CardContent.Clip = new RectangleGeometry(
            new Rect(0, 0, size.Width, size.Height), radius, radius);
    }

    /// <summary>
    /// Containers are recycled, so one tile serves many captures over its life.
    /// Cancelling the previous request matters twice: it stops a fast scroll
    /// queueing thousands of decodes, and it stops a late result from a previous
    /// record being applied to whatever this tile now shows.
    /// </summary>
    private async void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        CancelPending();

        if (e.NewValue is not CaptureTileViewModel tile) return;

        var cts = new CancellationTokenSource();
        _pending = cts;

        await tile.EnsureThumbnailAsync(cts.Token);
    }

    private void CancelPending()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }
}
