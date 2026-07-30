using System.Windows;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;

namespace Snipwhiz.App.Editor;

/// <summary>
/// A crop rectangle wearing an <see cref="Annotation"/>'s clothes.
///
/// <para>Exists so the crop tool gets <see cref="Handles"/> — handle positions,
/// which handle is opposite which, and the resize arithmetic including the aspect
/// and about-centre modifiers — instead of a second copy of all of it that has to
/// be kept in step and separately tested. A crop is an unrotated box, which is
/// exactly what that code already handles.</para>
///
/// <para>Never added to a document, never serialized, never drawn. It is a value
/// carrier, so <see cref="Render"/> deliberately does nothing.</para>
/// </summary>
internal sealed class CropProxy : Annotation
{
    private Size _size;

    public static CropProxy For(Rect crop) => new()
    {
        _size = crop.Size,
        Transform = new Matrix(1, 0, 0, 1, crop.X + crop.Width / 2, crop.Y + crop.Height / 2),
    };

    /// <summary>The rectangle this stands for, back in image space.</summary>
    public Rect Rect => new(
        Transform.OffsetX - _size.Width / 2,
        Transform.OffsetY - _size.Height / 2,
        _size.Width,
        _size.Height);

    public override Rect LocalBounds => new(-_size.Width / 2, -_size.Height / 2, _size.Width, _size.Height);

    public void Resize(Size size, Matrix transform)
    {
        _size = size;
        Transform = transform;
    }

    public override void Fit(Point from, Point to)
    {
        var rect = new Rect(from, to);
        _size = rect.Size;
        Transform = new Matrix(1, 0, 0, 1, rect.X + rect.Width / 2, rect.Y + rect.Height / 2);
    }

    public override GeometryState CaptureGeometry() => new RectangleGeometryState(_size);

    public override void RestoreGeometry(GeometryState state) => _size = ((RectangleGeometryState)state).Size;

    public override GeometryState GeometryForBounds(Size size) => new RectangleGeometryState(size);

    protected override bool HitTestLocal(Point local, double tolerance) => LocalBounds.Contains(local);

    public override void Render(DrawingContext dc) { }
}
