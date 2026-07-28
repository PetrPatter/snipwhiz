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
    private int _bindings;

    /// <summary>
    /// How many live tiles are currently showing this capture.
    ///
    /// One view model is shared by every tile that ever displays this capture —
    /// <see cref="LibraryViewModel"/> keys them by id — so "has the tile that
    /// decoded this gone away?" is the wrong question to ask before dropping the
    /// bitmap. During a resize the old container is discarded and a new one binds
    /// the same view model, and answering the wrong question released the bitmap
    /// the new tile had just finished decoding.
    ///
    /// UI thread only, which is where bind and release both run.
    /// </summary>
    public bool IsBound => _bindings > 0;

    public void AddBinding() => _bindings++;

    public void RemoveBinding() => _bindings--;

    public CaptureRecord Record => record;

    /// <summary>
    /// Points this tile at the capture as it now stands, and re-fetches its
    /// thumbnail.
    ///
    /// <para><b>Replacing the record is the part that is easy to miss.</b>
    /// <c>ThumbnailCache</c> resolves through <c>CaptureAssets.Display</c>, which
    /// decides between the render and the original by reading
    /// <c>FlatPath</c> <i>on the record it is handed</i>. Deleting the cached
    /// JPEG and re-fetching against the stale record regenerates a thumbnail of
    /// the un-annotated capture — the same visible failure, reached by a longer
    /// route.</para>
    /// </summary>
    public void Refresh(CaptureRecord updated)
    {
        record = updated;
        Raise(nameof(Record));
        Raise(nameof(Caption));
        Raise(nameof(Dimensions));
        Raise(nameof(TakenAt));

        Thumbnail = null;
        IsMissing = false;
        _loaded = false;

        // Not bound to anything on screen, so the next bind will fetch it. Fetching
        // now would decode a bitmap nothing is displaying.
        if (!IsBound) return;
        _ = ReloadAsync();
    }

    private async Task ReloadAsync()
    {
        await EnsureThumbnailAsync(CancellationToken.None);
        // Scrolled away while it decoded. Keeping it would retain a bitmap no tile
        // is showing, which is the leak the binding count exists to prevent.
        if (!IsBound) ReleaseThumbnail();
    }

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

    /// <summary>
    /// Drops the decoded bitmap when the tile showing this capture leaves the
    /// visual tree. Called from <c>CaptureTile.Release</c>, which documents why
    /// that is the only workable trigger.
    ///
    /// Without this, memory tracks how much of the library has ever been scrolled
    /// past rather than what is on screen: a decoded 320px thumbnail is ~256 KB,
    /// so a thousand of them is ~256 MB retained forever. Note that those pixels
    /// live in unmanaged WIC memory, not on the GC heap — the 1,000-capture sweep
    /// measured a 714 MB working set against a 4.3 MB managed heap, so heap size
    /// says nothing useful here and <c>retainedThumbnails</c> is the number to
    /// watch. Virtualizing the containers is worthless if the data they were
    /// bound to is never released.
    ///
    /// Re-showing the tile re-reads the cached JPEG, which is what the disk cache
    /// is for.
    /// </summary>
    public void ReleaseThumbnail()
    {
        if (_thumbnail is null) return;
        Thumbnail = null;
        _loaded = false;
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
                // WPF keeps a process-wide imaging cache keyed on the URI. A
                // thumbnail path never changes but its contents do — every time a
                // capture is edited — so without this a re-decode is served the
                // bitmap from before the edit and the tile silently stays stale.
                if (!Diagnostics.RefreshVerification.BreakImageCache)
                    bitmap.CreateOptions = BitmapCreateOptions.IgnoreImageCache;
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
