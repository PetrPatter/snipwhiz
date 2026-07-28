using System.IO;
using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.App.Diagnostics;

/// <summary>
/// Fills a library with synthetic captures so the virtualization gate has
/// something to scroll. Test infrastructure, not a feature.
///
/// Inert unless <c>SNIPWHIZ_SEED</c> is set, and it refuses to run unless
/// <c>SNIPWHIZ_ROOT</c> is set too — seeding a thousand fake captures into
/// someone's real library would be the sort of "helpful" act that is very hard to
/// undo.
///
/// <code>
/// $env:SNIPWHIZ_ROOT = "$env:TEMP\snipwhiz-verify"
/// $env:SNIPWHIZ_SEED = "1000"
/// </code>
///
/// <para><c>SNIPWHIZ_SEED_EDITED=n</c> additionally fakes an editor save on the
/// newest n captures: it writes a flat render in a deliberately unmistakable
/// colour and records it with <c>SetEditPaths</c>. There is no editor yet, and
/// this is the only way to prove that the tile, the preview and the clipboard all
/// resolve through <c>CaptureAssets.Display</c> — the routing has to be checked
/// before an editor exists to blame for getting it wrong.</para>
/// </summary>
internal static class LibrarySeed
{
    public static void RunIfRequested(CaptureStore store)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("SNIPWHIZ_SEED"), out var target) || target <= 0)
            return;

        if (string.IsNullOrEmpty(Environment.GetEnvironmentVariable("SNIPWHIZ_ROOT")))
            throw new InvalidOperationException(
                "SNIPWHIZ_SEED requires SNIPWHIZ_ROOT — refusing to seed the real library.");

        var existing = store.Count();

        // Distinct enough to tell tiles apart while scrolling, cheap enough that
        // a thousand of them cost nothing.
        for (var i = existing; i < target; i++)
        {
            var image = Swatch(320, 200, i);
            store.Save(image, $"seed{i % 7}", $"Seeded capture {i}");
        }

        FakeEdits(store);
    }

    /// <summary>
    /// Writes flat renders for the newest captures and records them, without an
    /// editor. Magenta, because the point is to be obvious at a glance: any tile,
    /// preview or paste still showing a muted swatch is a consumer that did not go
    /// through <c>Display</c>.
    /// </summary>
    private static void FakeEdits(CaptureStore store)
    {
        if (!int.TryParse(Environment.GetEnvironmentVariable("SNIPWHIZ_SEED_EDITED"), out var count) || count <= 0)
            return;

        foreach (var record in store.Recent(count))
        {
            var flat = store.Assets.Flat(record.Id);
            // Keyed on the file, not the column: after the fallback check deletes
            // the renders the rows still claim them, and skipping on the column
            // would make this un-rerunnable.
            if (File.Exists(flat)) continue;

            Directory.CreateDirectory(Path.GetDirectoryName(flat)!);

            var image = Magenta(record.Width, record.Height);
            File.WriteAllBytes(flat, Core.Imaging.PngEncoder.Encode(image.Bgra, image.Width, image.Height));

            store.SetEditPaths(
                record.Id,
                Path.GetRelativePath(store.Root, store.Assets.Project(record.Id)),
                Path.GetRelativePath(store.Root, flat),
                record.Width, record.Height,
                DateTimeOffset.UtcNow);
        }
    }

    private static CroppedImage Magenta(int width, int height)
    {
        var bgra = new byte[width * height * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 255;      // B
            bgra[i + 1] = 0;    // G
            bgra[i + 2] = 255;  // R
            bgra[i + 3] = 255;
        }
        return new CroppedImage(bgra, width, height, false);
    }

    private static CroppedImage Swatch(int width, int height, int seed)
    {
        var bgra = new byte[width * height * 4];
        byte b = (byte)(40 + seed * 7 % 180);
        byte g = (byte)(40 + seed * 13 % 180);
        byte r = (byte)(40 + seed * 29 % 180);

        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = b;
            bgra[i + 1] = g;
            bgra[i + 2] = r;
            bgra[i + 3] = 255;
        }
        return new CroppedImage(bgra, width, height, false);
    }
}
