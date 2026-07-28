using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;

namespace Snipwhiz.Core.Project;

/// <summary>
/// An annotation written by a newer build than the one reading it.
///
/// <para>Carried through load and save byte-intact instead of being dropped, so
/// opening a project in an older build and saving it does not silently destroy
/// work. This costs one retained <see cref="JsonElement"/> per object and prevents
/// the kind of data loss nobody notices until much later.</para>
///
/// <para>Deliberately inert: no bounds, never hit, never drawn. It is a passenger,
/// not something the user can select and be confused by. The consequence is that
/// an object an older build cannot understand is also invisible in it — better
/// than the alternative of rendering something wrong, and far better than deleting
/// it.</para>
/// </summary>
public sealed class UnknownAnnotation : Annotation
{
    /// <summary>
    /// The original JSON, cloned so it outlives the <see cref="JsonDocument"/> it
    /// was parsed from. Without the clone this is a use-after-dispose that reads as
    /// corrupt data.
    /// </summary>
    public required JsonElement Raw { get; init; }

    /// <summary>The type tag this build did not recognise. Kept for diagnostics.</summary>
    public required string TypeTag { get; init; }

    public override Rect LocalBounds => Rect.Empty;

    protected override bool HitTestLocal(Point local, double tolerance) => false;

    /// <summary>Nothing to capture: this object cannot be selected, so it cannot be reshaped.</summary>
    public override GeometryState CaptureGeometry() => new OpaqueGeometryState();

    public override void RestoreGeometry(GeometryState state) { }

    public override void Render(DrawingContext dc) { }
}
