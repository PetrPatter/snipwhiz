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
        Assets = new CaptureAssets(root);
        Directory.CreateDirectory(_root);
        _db = new LibraryDb(Path.Combine(_root, "library.db"));
    }

    public int SchemaVersion => _db.SchemaVersion;

    public string Root => _root;

    /// <summary>
    /// Path resolution for every file a capture owns. Anything deciding what to
    /// <i>show</i> goes through <see cref="CaptureAssets.Display"/>, not
    /// <see cref="ResolvePath"/>.
    /// </summary>
    public CaptureAssets Assets { get; }

    /// <summary>
    /// The immutable original. <see cref="CaptureRecord.FilePath"/> is relative to
    /// the store root so the folder stays movable.
    ///
    /// <para>Kept for the editor, which genuinely wants the unannotated capture as
    /// its source bitmap. If you are choosing an image to display, copy or export,
    /// this is the wrong method — see <see cref="Assets"/>.</para>
    /// </summary>
    public string ResolvePath(CaptureRecord record) => Assets.Original(record);

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
        try
        {
            _db.Insert(record);
        }
        catch
        {
            // The PNG is already on disk; if the row never lands, nothing will ever
            // reference that file again. Delete it rather than let a failing DB
            // silently fill the capture folder with orphans. Best-effort by design:
            // a failure to clean up must not replace the real fault the caller needs.
            try { File.Delete(absolutePath); }
            catch (Exception) { }
            throw;
        }
        return record;
    }

    public IReadOnlyList<CaptureRecord> Recent(int limit) => _db.Recent(limit);

    public IReadOnlyList<CaptureRecord> Page(CaptureRecord? after, int limit) => _db.Page(after, limit);

    public IReadOnlyList<CaptureRecord> Search(string query, int limit) => _db.Search(query, limit);

    /// <summary>Removes the row only. The caller owns the files — see spec 2a §4.7.</summary>
    public bool Delete(Guid id) => _db.Delete(id);

    public void Insert(CaptureRecord record) => _db.Insert(record);

    /// <summary>Records the result of an editor save. Spec 2b §4.12.</summary>
    public void SetEditPaths(
        Guid id, string projectPath, string? flatPath,
        int? flatWidth, int? flatHeight, DateTimeOffset editedUtc) =>
        _db.SetEditPaths(id, projectPath, flatPath, flatWidth, flatHeight, editedUtc);

    public int Count() => _db.Count();

    /// <summary>
    /// Size of the captures folder. Not a query — the schema has no size column —
    /// and deliberately scoped to <c>captures/</c> so the database, its WAL and
    /// the thumbnail cache are excluded.
    /// </summary>
    public long TotalBytes()
    {
        var dir = Path.Combine(_root, "captures");
        if (!Directory.Exists(dir)) return 0;
        return new DirectoryInfo(dir)
            .EnumerateFiles("*", SearchOption.AllDirectories)
            .Sum(f => f.Length);
    }

    public void Dispose() => _db.Dispose();
}
