using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using Snipwhiz.App.Editor;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Scene;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App.Tests;

/// <summary>
/// Stands up enough of the app for a control to behave like itself.
///
/// <para>These tests run against <b>realized</b> visuals in a shown window, not
/// against constructed objects. Half the subjects here are meaningless without
/// layout: <see cref="CanvasHost.Fit"/> divides by <c>ActualWidth</c>, the pill is
/// positioned from measured size, and <c>KeyEventArgs</c> needs a real
/// <see cref="PresentationSource"/>. A test over an unrealized control would pass
/// while asserting nothing.</para>
///
/// <para>The window is shown off-screen rather than hidden. <c>Visibility.Hidden</c>
/// skips arrange, which is the half that produces <c>ActualWidth</c>.</para>
/// </summary>
internal static class Harness
{
    /// <summary>A whole editor over a real capture in a throwaway library.</summary>
    public static void Editor(Action<EditorView> body) => Sta.Run(() =>
    {
        var root = Path.Combine(Path.GetTempPath(), "snipwhiz-app-tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new CaptureStore(root);
            var record = store.Save(Capture(120, 80), "test", "test");

            var editor = new EditorView(store, new Core.Settings(), root);
            Show(editor, window =>
            {
                editor.Open(record, Source(120, 80));
                // Open defers Fit to Loaded priority, so the view has no zoom until
                // the queue has run.
                Pump();
                body(editor);
            });
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    });

    /// <summary>A bare canvas over a document the caller owns, for view-level checks.</summary>
    public static void Canvas(Action<CanvasHost, SceneDocument> body) => Sta.Run(() =>
    {
        var canvas = new CanvasHost();
        var document = new SceneDocument { CaptureId = Guid.CreateVersion7() };

        Show(canvas, _ =>
        {
            canvas.Load(Source(120, 80), document);
            canvas.Fit();
            body(canvas, document);
        });
    });

    /// <summary>
    /// The <see cref="PresentationSource"/> of the window currently under test.
    /// Needed to build a <c>KeyEventArgs</c> that WPF will accept.
    /// </summary>
    public static PresentationSource Source(FrameworkElement element) =>
        PresentationSource.FromVisual(element)
        ?? throw new InvalidOperationException("The element is not in a shown window.");

    private static void Show(FrameworkElement content, Action<Window> body)
    {
        var window = new Window
        {
            Content = content,
            Width = 400,
            Height = 300,
            WindowStyle = WindowStyle.None,
            ShowInTaskbar = false,
            ShowActivated = false,
            // Off-screen rather than hidden: a hidden window does not arrange.
            Left = -10_000,
            Top = -10_000,
        };

        window.Show();
        try
        {
            window.UpdateLayout();
            body(window);
        }
        finally
        {
            window.Close();
        }
    }

    /// <summary>
    /// Runs the dispatcher queue down to Background, which is below the Loaded
    /// priority the editor defers its first <c>Fit</c> to.
    /// </summary>
    public static void Pump() =>
        Dispatcher.CurrentDispatcher.Invoke(() => { }, DispatcherPriority.Background);

    private static CroppedImage Capture(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return new CroppedImage(bgra, width, height, false);
    }

    private static BitmapSource Source(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var i = 3; i < pixels.Length; i += 4) pixels[i] = 255;

        var source = BitmapSource.Create(width, height, 96, 96, PixelFormats.Bgra32, null, pixels, stride);
        source.Freeze();
        return source;
    }
}
