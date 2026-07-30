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

    /// <summary>
    /// A callout's tail tip. The first handle that is neither a resizer nor the
    /// rotate handle; see <see cref="Annotation.ControlPoints"/>.
    /// </summary>
    Tail,
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
            // Anything else is a control point the object owns and this class knows
            // nothing about. The base implementation returns the centre, which is
            // what None used to fall through to.
            _ => annotation.ControlPoint(kind),
        };
    }

    /// <summary>The same position in image space.</summary>
    public static Point ImagePosition(Annotation annotation, HandleKind kind, double rotateGap) =>
        annotation.Transform.Transform(LocalPosition(annotation, kind, rotateGap));

    /// <summary>The result of a resize: a new shape, and the transform that keeps it in place.</summary>
    /// <param name="LocalCentre">
    /// Where the object's new origin sits in its <b>old</b> local frame — non-zero
    /// whenever the anchor is a corner rather than the centre. Anything the object
    /// stores in local coordinates has moved by exactly this much and has to be
    /// rebased, which is what <see cref="Annotation.Rebased"/> is for.
    /// </param>
    public readonly record struct Resized(Size Size, Matrix Transform, Vector LocalCentre);

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

        return new Resized(new Size(width, height), transform, new Vector(centre.X, centre.Y));
    }

    /// <summary>The transform that puts a local point of an object onto an image point.</summary>
    public static Matrix Anchored(Matrix transform, Point local, Point image)
    {
        // Rotation only, so the local point is mapped by the object's orientation
        // and not by wherever it currently sits.
        var rotation = transform;
        rotation.OffsetX = 0;
        rotation.OffsetY = 0;

        var mapped = rotation.Transform(local);
        transform.OffsetX = image.X - mapped.X;
        transform.OffsetY = image.Y - mapped.Y;
        return transform;
    }

    /// <summary>
    /// Settles a resize onto an object: gives it the new geometry, then finds the
    /// transform that keeps the anchor where it was, then rebases anything the object
    /// measures from its own origin.
    /// </summary>
    ///
    /// <remarks>
    /// <para><b><see cref="Resize"/>'s transform is a prediction, and for some types
    /// it is wrong.</b> It positions the object assuming its bounds will be exactly
    /// the size it was asked for. That holds for a rectangle. It does not hold for
    /// text or a callout, which answer a resize by changing <i>font size</i> — the
    /// width then comes from the glyphs and lands somewhere else entirely. The
    /// anchor was therefore computed against bounds the object never had, and a
    /// caption being resized skated around under the pointer instead of growing from
    /// its opposite corner.</para>
    ///
    /// <para>So the geometry is applied <b>first</b> and the transform derived from
    /// the bounds that actually resulted. That is why this mutates rather than
    /// returning a plan: there is no way to ask an annotation how big it would be
    /// without telling it.</para>
    ///
    /// <para>With <paramref name="aboutCentre"/> there is nothing to anchor — the
    /// centre is staying put by definition — so the predicted transform is already
    /// right.</para>
    /// </remarks>
    ///
    /// <param name="anchorImage">
    /// Where the opposite handle was in image space when the gesture began. Captured
    /// once at the start, not recomputed per frame, or it chases itself.
    /// </param>
    public static (GeometryState Geometry, Matrix Transform) Settle(
        Annotation annotation, Matrix startTransform, GeometryState startGeometry, Resized resized,
        HandleKind handle, Point anchorImage, bool aboutCentre)
    {
        var geometry = annotation.GeometryForBounds(resized.Size);
        annotation.RestoreGeometry(geometry);

        var transform = aboutCentre
            ? resized.Transform
            : Anchored(resized.Transform, LocalPosition(annotation, Opposite(handle), 0), anchorImage);

        // The true origin shift, read back from where the object actually ended up
        // rather than from Resize's prediction — the same reason as above, and the
        // callout tail depends on it being the real one.
        //
        // Measured against startTransform and applied to startGeometry, so it is the
        // whole shift since the gesture began rather than an increment. This runs on
        // every mouse-move; an increment applied to the previous frame's result
        // compounds, and the tail accelerates away instead of holding still.
        var inverse = startTransform;
        if (inverse.HasInverse)
        {
            inverse.Invert();
            var origin = inverse.Transform(new Point(transform.OffsetX, transform.OffsetY));
            geometry = annotation.Rebased(geometry, startGeometry, new Vector(origin.X, origin.Y));
            annotation.RestoreGeometry(geometry);
        }

        return (geometry, transform);
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
