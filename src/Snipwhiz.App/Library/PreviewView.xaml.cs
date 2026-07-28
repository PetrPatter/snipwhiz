using System.IO;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App.Library;

/// <summary>
/// Full-size view of one capture, hosted inside the library window rather than in
/// a window of its own. A second window would bring its own DPI handling, its own
/// backdrop call, its own placement and z-order rules, and a second thing to keep
/// in sync with the grid's selection — for one image and three controls.
/// </summary>
public partial class PreviewView : System.Windows.Controls.UserControl
{
    private readonly CaptureStore _store;
    private CaptureRecord? _record;
    private BitmapSource? _image;
    private CancellationTokenSource? _pending;

    public event Action? Dismissed;
    public event Action<CaptureRecord>? CopyRequested;
    public event Action<CaptureRecord>? DeleteRequested;
    public event Action<CaptureRecord>? EditRequested;

    public PreviewView() : this(null!) { }   // designer only

    public PreviewView(CaptureStore store)
    {
        _store = store;
        InitializeComponent();

        BackButton.Click += (_, _) => Close();
        CopyButton.Click += (_, _) => Copy();
        EditButton.Click += (_, _) =>
        {
            var record = _record;
            if (record is null) return;
            Close();
            EditRequested?.Invoke(record);
        };

        DeleteButton.Click += (_, _) =>
        {
            // Captured before Close clears it — the handler runs after.
            var record = _record;
            if (record is null) return;
            Close();
            DeleteRequested?.Invoke(record);
        };

        // Only a click on the empty surface itself, not one that bubbled up from
        // the image — otherwise clicking the picture dismisses it.
        ImageSurface.MouseLeftButtonUp += (_, e) =>
        {
            if (ReferenceEquals(e.OriginalSource, ImageSurface)) Close();
        };

        SizeChanged += (_, _) => ApplyScale();
    }

    public bool IsOpen => Visibility == Visibility.Visible;

    public async void Open(CaptureRecord record)
    {
        _record = record;
        _image = null;
        FullImage.Source = null;
        Visibility = Visibility.Visible;

        TitleText.Text = string.IsNullOrWhiteSpace(record.SourceTitle) ? "Capture" : record.SourceTitle;
        MetaText.Text = $"{record.Width} × {record.Height} · {record.CreatedUtc.ToLocalTime():dddd, d MMMM HH:mm}";
        StatusText.Text = "Loading…";
        CopyButton.IsEnabled = false;

        _pending?.Cancel();
        _pending?.Dispose();
        var cts = new CancellationTokenSource();
        _pending = cts;

        // Display, not the original: the preview is what the user opened the tile
        // to look at, so it shows the annotations if there are any.
        var path = _store.Assets.Display(record);
        try
        {
            // The full PNG, not the thumbnail scaled up: a lossy 320px preview
            // shown at 1200px is exactly the blurry look this product exists to
            // avoid. Decoded off the UI thread and frozen before it comes back.
            var frame = await Task.Run(() =>
            {
                using var stream = File.OpenRead(path);
                var decoded = BitmapFrame.Create(stream, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                decoded.Freeze();
                return (BitmapSource)decoded;
            }, cts.Token);

            if (cts.IsCancellationRequested || !ReferenceEquals(_pending, cts)) return;

            _image = frame;
            FullImage.Source = frame;
            StatusText.Text = string.Empty;
            CopyButton.IsEnabled = true;
            ApplyScale();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or NotSupportedException or FileFormatException)
        {
            StatusText.Text = "This capture could not be opened. Its file may have been moved or deleted.";
            CopyButton.IsEnabled = false;
        }
    }

    public void Close()
    {
        _pending?.Cancel();
        Visibility = Visibility.Collapsed;
        FullImage.Source = null;
        _image = null;
        _record = null;
        Dismissed?.Invoke();
    }

    /// <summary>
    /// Fits down when the capture is larger than the surface, and shows it at
    /// physical 1:1 otherwise.
    ///
    /// The 1:1 case is the subtle one. Captures are stored in physical pixels, but
    /// WPF lays out in DIPs — so sizing a 200×100 capture to 200×100 DIPs renders
    /// it at 250×125 physical on this machine's 125% panel, upscaled and blurry.
    /// That is spec 1 §4.3's rule broken by a different route, so the DIP size is
    /// derived from the monitor's own scale, exactly as the overlay does it.
    /// </summary>
    private void ApplyScale()
    {
        if (_image is null || _record is null) return;

        var scale = VisualTreeHelper.GetDpi(this).DpiScaleX;
        if (scale <= 0) scale = 1.0;

        var availablePhysicalWidth = Math.Max(1, ImageSurface.ActualWidth * scale);
        var availablePhysicalHeight = Math.Max(1, ImageSurface.ActualHeight * scale);

        double physicalWidth = _record.Width;
        double physicalHeight = _record.Height;

        var fits = physicalWidth <= availablePhysicalWidth && physicalHeight <= availablePhysicalHeight;
        var factor = fits
            ? 1.0
            : Math.Min(availablePhysicalWidth / physicalWidth, availablePhysicalHeight / physicalHeight);

        FullImage.Width = physicalWidth * factor / scale;
        FullImage.Height = physicalHeight * factor / scale;

        // At 1:1 any resampling filter is wrong by definition; when fitting down,
        // a filtered result is what the user expects to see.
        RenderOptions.SetBitmapScalingMode(FullImage,
            fits ? BitmapScalingMode.NearestNeighbor : BitmapScalingMode.HighQuality);
    }

    protected override void OnKeyDown(System.Windows.Input.KeyEventArgs e)
    {
        if (!IsOpen)
        {
            base.OnKeyDown(e);
            return;
        }

        if (e.Key == Key.Escape)
        {
            Close();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.C && Keyboard.Modifiers == ModifierKeys.Control)
        {
            Copy();
            e.Handled = true;
            return;
        }

        base.OnKeyDown(e);
    }

    /// <summary>
    /// Ctrl+C is handled by the window, not here: a UserControl's OnKeyDown only
    /// fires while it holds focus, which depends on where the user last clicked.
    /// </summary>
    public void CopyToClipboard() => Copy();

    /// <summary>
    /// The button and Ctrl+C both land here, and this is the only place either
    /// reaches the clipboard.
    /// </summary>
    private async void Copy()
    {
        if (_record is null || !CopyButton.IsEnabled) return;

        var record = _record;
        CopyButton.IsEnabled = false;
        var original = CopyButton.Content;
        CopyButton.Content = "Copying…";

        var result = await ClipboardCopier.CopyAsync(_store, record);

        CopyButton.Content = result switch
        {
            CopyResult.Copied => "Copied",
            _ => original,
        };

        StatusText.Text = result switch
        {
            CopyResult.Copied => string.Empty,
            CopyResult.ClipboardUnavailable =>
                "Another application is holding the clipboard. Try again in a moment.",
            _ => "This capture could not be read. Its file may have been moved or deleted.",
        };

        CopyRequested?.Invoke(record);

        // Only the label reverts; a failure leaves its message on screen.
        if (result == CopyResult.Copied)
        {
            await Task.Delay(1400);
            if (ReferenceEquals(_record, record)) CopyButton.Content = original;
        }

        CopyButton.IsEnabled = _image is not null;
    }
}
