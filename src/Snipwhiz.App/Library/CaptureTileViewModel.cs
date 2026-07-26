using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App.Library;

/// <summary>
/// One capture in the grid. Lives per record rather than per container, so a
/// thumbnail decoded once survives the tile being recycled and scrolled back to.
/// </summary>
public sealed class CaptureTileViewModel(CaptureRecord record, ThumbnailCache cache) : INotifyPropertyChanged
{
    private ImageSource? _thumbnail;
    private bool _isMissing;
    private bool _loaded;

    public CaptureRecord Record => record;

    public string Caption => string.IsNullOrWhiteSpace(record.SourceApp) ? "Capture" : record.SourceApp;

    public string Dimensions => $"{record.Width} × {record.Height}";

    public string TakenAt => record.CreatedUtc.ToLocalTime().ToString("HH:mm");

    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        private set { _thumbnail = value; Raise(); }
    }

    /// <summary>The original could not be read. Task 10 gives this its own affordance.</summary>
    public bool IsMissing
    {
        get => _isMissing;
        private set { _isMissing = value; Raise(); }
    }

    public async Task EnsureThumbnailAsync(CancellationToken ct)
    {
        if (_loaded) return;

        try
        {
            var path = await cache.GetOrCreateAsync(record, ct).ConfigureAwait(true);
            ct.ThrowIfCancellationRequested();

            // Decoded on the pool and frozen before it crosses back — an unfrozen
            // BitmapImage belongs to the thread that built it and throws on use here.
            var image = await Task.Run(() =>
            {
                var bitmap = new BitmapImage();
                bitmap.BeginInit();
                bitmap.CacheOption = BitmapCacheOption.OnLoad;
                bitmap.UriSource = new Uri(path);
                bitmap.EndInit();
                bitmap.Freeze();
                return (ImageSource)bitmap;
            }, ct).ConfigureAwait(true);

            Thumbnail = image;
            _loaded = true;
        }
        catch (OperationCanceledException)
        {
            // Scrolled out of view before it finished. Leave _loaded false so the
            // next bind retries.
        }
        catch (ImageDecodeException)
        {
            IsMissing = true;
            _loaded = true;
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Raise([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>A row of tiles. The App-side counterpart of <see cref="TileRow"/>.</summary>
public sealed class TileRowViewModel(IReadOnlyList<CaptureTileViewModel> tiles)
{
    public IReadOnlyList<CaptureTileViewModel> Tiles => tiles;
}
