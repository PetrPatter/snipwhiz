namespace Snipwhiz.Core.Storage;

/// <summary>
/// Every file a capture owns, and — more importantly — the single answer to
/// "which one do I show?".
///
/// <para>Before this existed, three call sites resolved a capture independently
/// and all three reached for the original PNG: <c>ThumbnailCache.Generate</c>,
/// <c>ClipboardCopier.CopyAsync</c> and <c>PreviewView.Open</c>. Redirecting only
/// the thumbnail to the annotated render — which is what the first draft of spec
/// 2b did — ships a library whose grid shows the edited image while its preview
/// and its clipboard hand back the un-annotated one. That is the highest-traffic
/// path in the product, silently wrong.</para>
///
/// <para>Deliberately independent of <see cref="CaptureStore"/>: it needs a root
/// directory and nothing else, so path resolution is testable without standing up
/// a database.</para>
/// </summary>
public sealed class CaptureAssets(string root)
{
    public string Root => root;

    /// <summary>The immutable capture. Only the editor wants this — everything else wants <see cref="Display"/>.</summary>
    public string Original(CaptureRecord record) => Path.Combine(root, record.FilePath);

    /// <summary>
    /// What the user should see: the flattened render if this capture has been
    /// edited, otherwise the capture itself.
    ///
    /// <para><see cref="CaptureRecord.FlatPath"/> is the authority on whether a
    /// render was ever saved — there is no second source of truth and no directory
    /// probing for unedited captures, which is the overwhelming majority. The
    /// existence check applies only to captures that claim a render, and exists so
    /// that a deleted or half-written flat file degrades to the original rather
    /// than to a broken tile.</para>
    /// </summary>
    public string Display(CaptureRecord record)
    {
        if (record.FlatPath is null) return Original(record);
        var flat = Path.Combine(root, record.FlatPath);
        return File.Exists(flat) ? flat : Original(record);
    }

    /// <summary>Where this capture's annotations live, whether or not they exist yet.</summary>
    public string Project(Guid id) => Path.Combine(root, "projects", $"{id:D}.ssproj");

    /// <summary>Where this capture's flattened render belongs, whether or not it exists yet.</summary>
    public string Flat(Guid id) => Path.Combine(root, "flat", $"{id:D}.png");

    public string Thumbnail(Guid id) => Path.Combine(root, "thumbs", $"{id:D}.jpg");

    /// <summary>
    /// Every file belonging to this capture, for delete.
    ///
    /// <para>Uses the canonical project and flat locations rather than the record's
    /// columns, so a file orphaned by a save that crashed between writing and
    /// recording still gets cleaned up. Deleting a path that does not exist is the
    /// caller's problem and a cheap one; leaving a file behind forever is not.</para>
    /// </summary>
    public IReadOnlyList<string> All(CaptureRecord record) =>
    [
        Original(record),
        Thumbnail(record.Id),
        Flat(record.Id),
        Project(record.Id),
    ];
}
