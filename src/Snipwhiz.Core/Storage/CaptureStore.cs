using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Imaging;

namespace Snipwhiz.Core.Storage;

/// <summary>Writes the immutable original PNG and records it. Never modifies a saved file.</summary>
public sealed class CaptureStore : IDisposable
{
    private readonly string _root;
    private readonly LibraryDb _db;
    private readonly Func<Guid> _newId;

    /// <param name="newId">
    /// Seam for tests. Defaults to Guid.CreateVersion7 — time-ordered, which is
    /// the only property the store needs. Injectable so the immutability guard
    /// in Save can be proven rather than assumed.
    /// </param>
    public CaptureStore(string root, Func<Guid>? newId = null)
    {
        _root = root;
        _newId = newId ?? Guid.CreateVersion7;
        Directory.CreateDirectory(_root);
        _db = new LibraryDb(Path.Combine(_root, "library.db"));
    }

    public int SchemaVersion => _db.SchemaVersion;

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snipwhiz");

    public CaptureRecord Save(CroppedImage image, string sourceApp, string sourceTitle)
    {
        var id = _newId();
        var now = DateTimeOffset.UtcNow;

        var relativeDir = Path.Combine("captures", now.ToString("yyyy"), now.ToString("MM"));
        Directory.CreateDirectory(Path.Combine(_root, relativeDir));

        var relativePath = Path.Combine(relativeDir, $"{id:D}.png");
        var absolutePath = Path.Combine(_root, relativePath);

        // FileMode.CreateNew, not WriteAllBytes: a saved capture is immutable, so a
        // colliding id must fail loudly rather than quietly destroy the earlier one.
        using (var file = new FileStream(absolutePath, FileMode.CreateNew, FileAccess.Write))
        {
            file.Write(PngEncoder.Encode(image.Bgra, image.Width, image.Height));
        }

        var record = new CaptureRecord(id, now, image.Width, image.Height, sourceApp, sourceTitle, relativePath);
        _db.Insert(record);
        return record;
    }

    public IReadOnlyList<CaptureRecord> Recent(int limit) => _db.Recent(limit);

    public void Dispose() => _db.Dispose();
}
