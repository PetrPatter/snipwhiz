using System.Windows;
using System.Windows.Media;

namespace Snipwhiz.Core.Annotations;

public enum HandleKind
{
    None,
    TopLeft, Top, TopRight,
    Right,
    BottomRight, Bottom, BottomLeft,
    Left,
    Rotate,
}

/// <summary>
/// Where an object's grab handles are, and what dragging one does to it.
///
/// <para>Pure geometry, in Core, so the awkward part is unit-tested against
/// hand-computed points before any mouse is involved. A resize on a rotated object
/// is the case that goes wrong: the handle moves along the object's own axes, not
/// the screen's, and the object's centre moves as a consequence of the corner
/// opposite staying put.</para>
///
/// <para>Everything here works in the object's <b>local</b> space — geometry centred
/// on the origin, unrotated — which is what makes it tractable. The caller converts
/// through <see cref="Annotation.Transform"/> at the edges.</para>
/// </summary>
public static class Handles
{
    /// <summary>The eight resize handles, in no particular order.</summary>
    public static readonly HandleKind[] Resizers =
    [
        HandleKind.TopLeft, HandleKind.Top, HandleKind.TopRight, HandleKind.Right,
        HandleKind.BottomRight, HandleKind.Bottom, HandleKind.BottomLeft, HandleKind.Left,
    ];

    /// <summary>
    /// A handle's position in the object's own space.
    /// </summary>
    /// <param name="rotateGap">
    /// How far the rotate handle sits beyond the top edge, in image pixels. The
    /// caller converts a constant screen distance, so the handle stays the same
    /// distance from the object on screen at every zoom.
    /// </param>
    public static Point LocalPosition(Annotation annotation, HandleKind kind, double rotateGap)
    {
        var b = annotation.LocalBounds;
        return kind switch
        {
            HandleKind.TopLeft => b.TopLeft,
            HandleKind.Top => new Point(b.X + b.Width / 2, b.Y),
            HandleKind.TopRight => b.TopRight,
            HandleKind.Right => new Point(b.Right, b.Y + b.Height / 2),
            HandleKind.BottomRight => b.BottomRight,
            HandleKind.Bottom => new Point(b.X + b.Width / 2, b.Bottom),
            HandleKind.BottomLeft => b.BottomLeft,
            HandleKind.Left => new Point(b.X, b.Y + b.Height / 2),
            HandleKind.Rotate => new Point(b.X + b.Width / 2, b.Y - rotateGap),
            _ => new Point(b.X + b.Width / 2, b.Y + b.Height / 2),
        };
    }

    /// <summary>The same position in image space.</summary>
    public static Point ImagePosition(Annotation annotation, HandleKind kind, double rotateGap) =>
        annotation.Transform.Transform(LocalPosition(annotation, kind, rotateGap));

    /// <summary>The result of a resize: a new shape, and the transform that keeps it in place.</summary>
    public readonly record struct Resized(Size Size, Matrix Transform);

    /// <summary>
    /// Drags one handle to a point, given in the object's local space.
    ///
    /// <para>Normally the corner opposite the handle stays put and the object's
    /// centre moves to the midpoint of the two — which is why a resize changes the
    /// transform as well as the size. With <paramref name="aboutCentre"/> the centre
    /// stays put instead and the object grows both ways.</para>
    ///
    /// <para>Edge handles move one axis and leave the other alone.
    /// <paramref name="preserveAspect"/> keeps the original proportions, driven by
    /// whichever axis moved further so the shape follows the pointer.</para>
    ///
    /// <para>Size is clamped to a minimum rather than allowed through zero: a
    /// rectangle dragged inside-out would otherwise invert, and a zero-sized one
    /// can never be grabbed again.</para>
    /// </summary>
    public static Resized Resize(
        Annotation annotation, HandleKind kind, Point local,
        bool preserveAspect = false, bool aboutCentre = false, double minimum = 4)
    {
        var b = annotation.LocalBounds;
        var opposite = LocalPosition(annotation, Opposite(kind), 0);

        var movesX = kind is HandleKind.Left or HandleKind.Right
            or HandleKind.TopLeft or HandleKind.TopRight
            or HandleKind.BottomLeft or HandleKind.BottomRight;
        var movesY = kind is HandleKind.Top or HandleKind.Bottom
            or HandleKind.TopLeft or HandleKind.TopRight
            or HandleKind.BottomLeft or HandleKind.BottomRight;

        double width, height;
        Point centre;

        if (aboutCentre)
        {
            width = movesX ? Math.Abs(local.X) * 2 : b.Width;
            height = movesY ? Math.Abs(local.Y) * 2 : b.Height;
            centre = new Point(0, 0);
        }
        else
        {
            width = movesX ? Math.Abs(local.X - opposite.X) : b.Width;
            height = movesY ? Math.Abs(local.Y - opposite.Y) : b.Height;
            centre = new Point(
                movesX ? (local.X + opposite.X) / 2 : 0,
                movesY ? (local.Y + opposite.Y) / 2 : 0);
        }

        if (preserveAspect && b.Width > 0 && b.Height > 0 && movesX && movesY)
        {
            var aspect = b.Width / b.Height;
            // Follow whichever axis the pointer pushed further, so the shape tracks
            // the mouse rather than fighting it.
            if (width / aspect >= height) height = width / aspect;
            else width = height * aspect;

            if (!aboutCentre)
            {
                // The opposite corner is the anchor, so recompute the centre from
                // the corrected extent rather than from where the pointer actually is.
                centre = new Point(
                    opposite.X + Math.Sign(local.X - opposite.X) * width / 2,
                    opposite.Y + Math.Sign(local.Y - opposite.Y) * height / 2);
            }
        }

        width = Math.Max(width, minimum);
        height = Math.Max(height, minimum);

        var transform = annotation.Transform;
        // Through the transform, not added to its offset. The local centre is in the
        // object's own rotated frame; adding it straight to the image-space offset
        // is identical on an unrotated object and slides every rotated one.
        var imageCentre = transform.Transform(centre);
        transform.OffsetX = imageCentre.X;
        transform.OffsetY = imageCentre.Y;

        return new Resized(new Size(width, height), transform);
    }

    /// <summary>
    /// The transform that turns an object to face an image-space point, about its
    /// own centre.
    /// </summary>
    /// <param name="snapDegrees">Non-zero to snap, as Shift does.</param>
    public static Matrix RotateToward(Annotation annotation, Point imagePoint, double snapDegrees = 0)
    {
        var centre = new Point(annotation.Transform.OffsetX, annotation.Transform.OffsetY);
        var v = imagePoint - centre;
        if (v.Length < 1e-6) return annotation.Transform;

        // The rotate handle sits above the object, so straight up is zero.
        var degrees = Math.Atan2(v.X, -v.Y) * 180 / Math.PI;
        if (snapDegrees > 0) degrees = Math.Round(degrees / snapDegrees) * snapDegrees;

        var rotated = Matrix.Identity;
        rotated.Rotate(degrees);
        rotated.Translate(centre.X, centre.Y);
        return rotated;
    }

    /// <summary>The handle across the object from this one; the anchor a resize pivots on.</summary>
    public static HandleKind Opposite(HandleKind kind) => kind switch
    {
        HandleKind.TopLeft => HandleKind.BottomRight,
        HandleKind.Top => HandleKind.Bottom,
        HandleKind.TopRight => HandleKind.BottomLeft,
        HandleKind.Right => HandleKind.Left,
        HandleKind.BottomRight => HandleKind.TopLeft,
        HandleKind.Bottom => HandleKind.Top,
        HandleKind.BottomLeft => HandleKind.TopRight,
        HandleKind.Left => HandleKind.Right,
        _ => HandleKind.None,
    };
}
