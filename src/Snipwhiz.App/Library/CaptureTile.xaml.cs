using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App.Library;

public partial class CaptureTile : System.Windows.Controls.UserControl
{
    private CancellationTokenSource? _pending;
    private CaptureTileViewModel? _boundTo;

    public CaptureTile()
    {
        InitializeComponent();
        DataContextChanged += (_, _) => Bind();
        Loaded += (_, _) => Bind();
        Unloaded += (_, _) => Release();
        CardContent.SizeChanged += (_, e) => ClipToRoundedCorners(e.NewSize);

        RemoveButton.Click += (_, e) =>
        {
            // Stop the click reaching the grid handler, which would otherwise try
            // to open a preview of the capture being removed.
            e.Handled = true;
            if (DataContext is CaptureTileViewModel model) RemoveRequested?.Invoke(model.Record);
        };
    }

    /// <summary>
    /// Raised for a capture whose original file is gone (spec 2a §4.12). Static
    /// because tiles are created by a DataTemplate, so there is no construction
    /// site to wire an instance event at.
    /// </summary>
    public static event Action<CaptureRecord>? RemoveRequested;

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
    /// Requests the thumbnail for whatever capture this tile now shows.
    ///
    /// Idempotent, because it has two triggers. <c>DataContextChanged</c> is the
    /// normal one; <c>Loaded</c> covers a tile that was released while unloaded
    /// and then put back with the same capture, where no DataContext transition
    /// occurs and the tile would otherwise stay blank forever. Without the
    /// <see cref="_boundTo"/> check the two would race on first display, the
    /// second cancelling the first's decode.
    /// </summary>
    private async void Bind()
    {
        if (DataContext is not CaptureTileViewModel tile)
        {
            CancelPending();
            Detach();
            return;
        }

        if (ReferenceEquals(_boundTo, tile)) return;

        // Cancelling the previous request matters twice: it stops a fast scroll
        // queueing thousands of decodes, and it stops a late result from a
        // previous record being applied to whatever this tile now shows.
        CancelPending();
        Detach();

        _boundTo = tile;
        tile.AddBinding();

        var cts = new CancellationTokenSource();
        _pending = cts;

        await tile.EnsureThumbnailAsync(cts.Token);
    }

    /// <summary>
    /// Drops the decoded bitmap once this tile leaves the tree.
    ///
    /// This is the only point at which a thumbnail is ever released, and it has to
    /// be here rather than on a DataContext transition. Tiles sit inside a
    /// non-virtualizing <c>ItemsControl</c> per row, so when the outer panel
    /// recycles a row container that inner control <i>regenerates</i> its children
    /// instead of rebinding them: each tile sees exactly one DataContext change in
    /// its life, null to a view model, and is then discarded. An old-value release
    /// therefore never fires — measured at <c>retainedThumbnails=1000</c> against
    /// <c>realizedTilesPeak=21</c>, for a 714 MB working set with a 4 MB managed
    /// heap.
    /// </summary>
    private void Release()
    {
        CancelPending();
        Detach();
    }

    /// <summary>
    /// Gives up this tile's claim on its view model, and drops the bitmap once no
    /// tile is claiming it any more.
    ///
    /// <para>Both halves of that are needed. The release is <b>deferred</b> because
    /// growing the window discards containers and builds new ones, so a capture
    /// that stays on screen throughout is briefly held by nothing; released
    /// immediately, it goes black and no DataContext transition occurs to
    /// re-request it. And the deferred check counts <b>bindings on the view
    /// model</b> rather than asking whether this element survived, because one view
    /// model is shared by every tile that shows that capture: checking this element
    /// released the bitmap the replacement tile had just decoded, which is the same
    /// black tile by a different route.</para>
    ///
    /// <para>Calling <see cref="Bind"/> from the callback covers the third case —
    /// this element is still on screen, so it re-attaches to whatever it now shows
    /// before the count is read.</para>
    /// </summary>
    private void Detach()
    {
        // Deliberately _boundTo rather than DataContext. WPF severs the inherited
        // DataContext when it disconnects the container, and does so without
        // raising DataContextChanged here — so by the time Unloaded runs the
        // property no longer holds the view model. Instrumenting the sweep showed
        // 993 unloads against 0 releases for exactly this reason. _boundTo was
        // captured at bind time and still points at the capture this tile showed.
        var releasing = _boundTo;
        if (releasing is null) return;

        _boundTo = null;
        releasing.RemoveBinding();

        // Negative control for the resize gate: dropping it here is what turned
        // on-screen tiles black.
        if (Diagnostics.ResizeVerification.BreakRelease)
        {
            releasing.ReleaseThumbnail();
            return;
        }

        Dispatcher.BeginInvoke(DispatcherPriority.Background, () =>
        {
            if (IsLoaded) Bind();
            if (!releasing.IsBound) releasing.ReleaseThumbnail();
        });
    }

    private void CancelPending()
    {
        _pending?.Cancel();
        _pending?.Dispose();
        _pending = null;
    }
}
