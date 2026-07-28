using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Snipwhiz.Core.Scene;

namespace Snipwhiz.Core.Imaging;

/// <summary>
/// Renders a scene over its capture and writes the result.
///
/// <para>Lives in Core beside the annotation model and calls the same
/// <c>Annotation.Render</c> the on-screen canvas does. That is not tidiness: two
/// render implementations is how "the export doesn't match what I drew" becomes a
/// whole class of bug with no natural check. One implementation makes the class
/// impossible, and the WYSIWYG gate exists to prove it stays that way.</para>
///
/// <para>Requires an STA thread — <see cref="RenderTargetBitmap"/> is part of the
/// WPF render stack. The save pipeline gives it a dedicated one rather than
/// spending tens of milliseconds of the UI thread at the moment a window closes.</para>
/// </summary>
public static class Flattener
{
    /// <summary>
    /// Composites the scene onto the capture at full resolution.
    ///
    /// <para>The result is frozen, so it can cross back to the thread that asked
    /// for it.</para>
    /// </summary>
    public static BitmapSource Render(BitmapSource source, SceneDocument document)
    {
        var crop = CropOf(source, document);

        var visual = new DrawingVisual();
        using (var dc = visual.RenderOpen())
        {
            // The crop is expressed in image pixels and the output starts at its
            // top-left, so everything shifts by the crop origin. Annotation geometry
            // is in image space too and rides along, which is why a crop moves the
            // objects with the picture instead of sliding them across it.
            dc.PushTransform(new TranslateTransform(-crop.X, -crop.Y));

            // Drawn at pixel size into a 96-DPI target, so one DIP is one image
            // pixel and the flattener applies no scale of its own.
            dc.DrawImage(source, new Rect(0, 0, source.PixelWidth, source.PixelHeight));

            foreach (var annotation in document.InPaintOrder()) annotation.Render(dc);

            dc.Pop();
        }

        var target = new RenderTargetBitmap(
            (int)Math.Round(crop.Width), (int)Math.Round(crop.Height),
            96, 96, PixelFormats.Pbgra32);
        target.Render(visual);
        target.Freeze();
        return target;
    }

    /// <summary>Renders and writes a PNG, replacing any existing one atomically.</summary>
    public static void Save(string path, BitmapSource source, SceneDocument document)
    {
        var rendered = Render(source, document);

        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temp = $"{path}.{Guid.NewGuid():N}.tmp";
        try
        {
            using (var stream = File.Create(temp))
            {
                // WPF's encoder, not the project's own PngEncoder, which takes
                // straight BGRA bytes. RenderTargetBitmap only produces Pbgra32 —
                // premultiplied — and handing those to an encoder expecting straight
                // alpha silently darkens every translucent pixel. Today the canvas
                // is opaque throughout so the two agree, which is exactly what makes
                // it a trap: it would start being wrong the first time an annotation
                // had partial alpha at the image edge.
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(rendered));
                encoder.Save(stream);
            }
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            try { File.Delete(temp); } catch (IOException) { }
            throw;
        }
    }

    /// <summary>
    /// The crop, clamped to the capture and never empty.
    ///
    /// <para>A crop reaching outside the source would render transparent margins,
    /// and a zero-width one would throw inside <see cref="RenderTargetBitmap"/>.
    /// Both are reachable from a hand-edited project file.</para>
    /// </summary>
    private static Rect CropOf(BitmapSource source, SceneDocument document)
    {
        var full = new Rect(0, 0, source.PixelWidth, source.PixelHeight);
        if (document.Crop is not { } crop) return full;

        crop.Intersect(full);
        if (crop.IsEmpty || crop.Width < 1 || crop.Height < 1) return full;
        return crop;
    }
}
