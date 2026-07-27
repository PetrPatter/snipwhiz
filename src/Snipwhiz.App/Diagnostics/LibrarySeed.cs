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
        if (existing >= target) return;

        // Distinct enough to tell tiles apart while scrolling, cheap enough that
        // a thousand of them cost nothing.
        for (var i = existing; i < target; i++)
        {
            var image = Swatch(320, 200, i);
            store.Save(image, $"seed{i % 7}", $"Seeded capture {i}");
        }
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
