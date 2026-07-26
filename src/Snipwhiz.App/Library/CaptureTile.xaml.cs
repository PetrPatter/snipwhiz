using System.Windows;
using System.Windows.Controls;

namespace Snipwhiz.App.Library;

public partial class CaptureTile : System.Windows.Controls.UserControl
{
    private CancellationTokenSource? _pending;

    public CaptureTile()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Unloaded += (_, _) => CancelPending();
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
