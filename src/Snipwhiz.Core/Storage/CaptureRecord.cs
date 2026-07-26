namespace Snipwhiz.Core.Storage;

/// <param name="FilePath">Relative to the store root, so the folder stays movable.</param>
public sealed record CaptureRecord(
    Guid Id,
    DateTimeOffset CreatedUtc,
    int Width,
    int Height,
    string SourceApp,
    string SourceTitle,
    string FilePath);
