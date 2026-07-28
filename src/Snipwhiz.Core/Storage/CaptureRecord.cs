namespace Snipwhiz.Core.Storage;

/// <param name="FilePath">Relative to the store root, so the folder stays movable.</param>
/// <param name="ProjectPath">
/// The <c>.ssproj</c> holding this capture's annotations, relative to the root.
/// Null until the capture is first edited. Schema v3.
/// </param>
/// <param name="FlatPath">
/// The rendered result of those annotations, relative to the root. Null until a
/// first successful save — a failed flatten leaves the project written and this
/// null, and callers fall back to the original (spec 2b §4.12).
/// </param>
/// <param name="FlatWidth">
/// Size of the flattened render, which is <i>larger</i> than the capture when a
/// border or edge effect is applied (spec 2b §4.10). Anything that sizes or scales
/// the displayed image must prefer these over <paramref name="Width"/>.
/// </param>
/// <param name="EditedUtc">
/// When the capture was last saved from the editor. Deliberately not an ordering
/// key — the library stays sorted by <paramref name="CreatedUtc"/>, because it is
/// a record of when captures were taken.
/// </param>
public sealed record CaptureRecord(
    Guid Id,
    DateTimeOffset CreatedUtc,
    int Width,
    int Height,
    string SourceApp,
    string SourceTitle,
    string FilePath,
    string? ProjectPath = null,
    string? FlatPath = null,
    int? FlatWidth = null,
    int? FlatHeight = null,
    DateTimeOffset? EditedUtc = null);
