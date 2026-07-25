namespace Snipwhiz.Core.Capture;

/// <param name="Bgra">Top-down BGRA32, stride Width * 4.</param>
/// <param name="HasUncoveredPixels">
/// True when the region overlaps part of the virtual bounding box that no
/// display covers. Those pixels are undefined, so the caller warns rather
/// than silently saving black bands.
/// </param>
public sealed record CroppedImage(byte[] Bgra, int Width, int Height, bool HasUncoveredPixels);
