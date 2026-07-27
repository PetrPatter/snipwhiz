using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Imaging;
using Snipwhiz.Core.Storage;

namespace Snipwhiz.Core.Tests;

/// <summary>
/// Builds a library of known shape. Test infrastructure, not a product feature —
/// Task 6 reuses it to seed the 1,000-capture set the virtualization gate needs.
///
/// Deterministic by construction: no Random, no UtcNow. Timestamps are derived
/// from the row index against a base instant the caller supplies, so a failure
/// reproduces exactly.
/// </summary>
public static class LibrarySeeder
{
    public static readonly DateTimeOffset Base =
        new(2026, 7, 20, 12, 0, 0, TimeSpan.Zero);

    /// <summary>
    /// Inserts <paramref name="count"/> rows one minute apart, oldest first.
    /// </summary>
    /// <param name="writeFiles">
    /// When true, writes a real 8x4 PNG per row so decode and TotalBytes have
    /// something to read. Off by default — most query tests never open a file.
    /// </param>
    public static IReadOnlyList<CaptureRecord> Seed(
        LibraryDb db,
        int count,
        DateTimeOffset? start = null,
        TimeSpan? step = null,
        Func<int, (string App, string Title)>? describe = null,
        string? root = null,
        bool writeFiles = false)
    {
        var from = start ?? Base;
        var gap = step ?? TimeSpan.FromMinutes(1);
        var records = new List<CaptureRecord>(count);

        for (var i = 0; i < count; i++)
        {
            var (app, title) = describe?.Invoke(i) ?? ($"app{i % 3}", $"Window {i}");
            var id = Guid.CreateVersion7();
            var relative = Path.Combine("captures", "2026", "07", $"{id:D}.png");

            if (writeFiles)
            {
                if (root is null)
                    throw new ArgumentNullException(nameof(root), "writeFiles requires a root.");
                var absolute = Path.Combine(root, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(absolute)!);
                File.WriteAllBytes(absolute, SamplePng());
            }

            var record = new CaptureRecord(id, from + gap * i, 8, 4, app, title, relative);
            db.Insert(record);
            records.Add(record);
        }

        return records;
    }

    /// <summary>Seeds rows that all share one timestamp, to exercise the id tiebreak.</summary>
    public static IReadOnlyList<CaptureRecord> SeedSameInstant(LibraryDb db, int count)
        => Seed(db, count, step: TimeSpan.Zero);

    private static byte[]? _cachedPng;

    private static byte[] SamplePng()
    {
        if (_cachedPng is not null) return _cachedPng;
        var bgra = new byte[8 * 4 * 4];
        for (var i = 0; i < bgra.Length; i += 4)
        {
            bgra[i] = 20;
            bgra[i + 1] = 40;
            bgra[i + 2] = 60;
            bgra[i + 3] = 255;
        }
        return _cachedPng = PngEncoder.Encode(bgra, 8, 4);
    }

    public static CroppedImage Image(int w = 8, int h = 4)
    {
        var bgra = new byte[w * h * 4];
        for (var i = 3; i < bgra.Length; i += 4) bgra[i] = 255;
        return new CroppedImage(bgra, w, h, false);
    }
}
