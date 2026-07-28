using System.Windows;
using Snipwhiz.Core.Annotations;

namespace Snipwhiz.Core.Scene;

/// <summary>
/// Everything an editor session holds: which capture it annotates, the objects on
/// it, and the document-level operations that change what the canvas <i>is</i>.
///
/// <para>The capture itself is not here. It is immutable on disk and referenced by
/// id, which is what makes editing non-destructive by construction rather than by
/// discipline.</para>
/// </summary>
public sealed class SceneDocument
{
    /// <summary>
    /// Bumped when the on-disk shape changes in a way a reader must know about.
    /// Present from the first write, before there is anything to migrate — a format
    /// that starts unversioned cannot be fixed later without guessing.
    /// </summary>
    public const int CurrentSchema = 1;

    public required Guid CaptureId { get; init; }

    /// <summary>As read from disk, so a file from a newer build round-trips honestly.</summary>
    public int Schema { get; init; } = CurrentSchema;

    public List<Annotation> Annotations { get; init; } = [];

    /// <summary>
    /// The visible region of the capture, in image pixels. Null means uncropped.
    ///
    /// <para>Stored rather than applied: the original is never rewritten, so
    /// un-cropping restores both the pixels and any annotations that were outside
    /// the rectangle (spec §4.10).</para>
    /// </summary>
    public Rect? Crop { get; set; }

    /// <summary>Objects in paint order, back to front.</summary>
    public IEnumerable<Annotation> InPaintOrder() => Annotations.OrderBy(a => a.ZIndex);
}
