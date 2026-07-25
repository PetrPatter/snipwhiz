using Snipwhiz.Core.Capture;
using Snipwhiz.Core.Imaging;

namespace Snipwhiz.Core.Storage;

/// <summary>Writes the immutable original PNG and records it. Never modifies a saved file.</summary>
public sealed class CaptureStore : IDisposable
{
    private readonly string _root;
    private readonly LibraryDb _db;

    public CaptureStore(string root)
    {
        _root = root;
        Directory.CreateDirectory(_root);
        _db = new LibraryDb(Path.Combine(_root, "library.db"));
    }

    public int SchemaVersion => _db.SchemaVersion;

    public static string DefaultRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Snipwhiz");

    public CaptureRecord Save(CroppedImage image, string sourceApp, string sourceTitle)
    {
        // UUIDv7 is time-ordered, which is the only property we need.
        var id = Guid.CreateVersion7();
        var now = DateTimeOffset.UtcNow;

        var relativeDir = Path.Combine("captures", now.ToString("yyyy"), now.ToString("MM"));
        Directory.CreateDirectory(Path.Combine(_root, relativeDir));

        var relativePath = Path.Combine(relativeDir, $"{id:D}.png");
        File.WriteAllBytes(Path.Combine(_root, relativePath), PngEncoder.Encode(image.Bgra, image.Width, image.Height));

        var record = new CaptureRecord(id, now, image.Width, image.Height, sourceApp, sourceTitle, relativePath);
        _db.Insert(record);
        return record;
    }

    public IReadOnlyList<CaptureRecord> Recent(int limit) => _db.Recent(limit);

    public void Dispose() => _db.Dispose();
}
