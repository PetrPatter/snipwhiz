using System.Windows;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// A snapshot of one annotation's shape, small enough to sit in an undo entry.
///
/// <para>A record, so equality is by value and a command can tell a real change
/// from a drag that ended where it began.</para>
/// </summary>
public abstract record GeometryState;

/// <summary>
/// Named for the state, not the shape: <c>System.Windows.Media.RectangleGeometry</c>
/// already exists and is a different thing entirely.
/// </summary>
public sealed record RectangleGeometryState(Size Size) : GeometryState;

public sealed record EllipseGeometryState(Size Size) : GeometryState;

/// <summary>
/// A line is a vector, not a box. <paramref name="Delta"/> runs from one end to the
/// other, and the object is centred on its own midpoint so rotation behaves the
/// same as every other shape.
/// </summary>
public sealed record LineGeometryState(Vector Delta) : GeometryState;

/// <summary>
/// Text carries its string as geometry, because the string is what decides the
/// object's size — so undoing a typed word is a geometry change like any other and
/// needs no command of its own.
/// </summary>
public sealed record TextGeometryState(string Text, double FontSize) : GeometryState;

/// <summary>
/// A pixelate is a box plus a block size, and the block size is geometry rather
/// than style for the same reason text's font size is: it changes how big the thing
/// is, and undo has to put it back.
/// </summary>
public sealed record PixelateGeometryState(Size Size, double BlockSize) : GeometryState;

/// <summary>The same shape as a pixelate's, for the same reason.</summary>
public sealed record BlurGeometryState(Size Size, double Radius) : GeometryState;

/// <summary>
/// A magnifier is two rectangles: the lens, which is <paramref name="Size"/> and the
/// transform every annotation has, and the subject, which is
/// <paramref name="SourceCentre"/> in absolute image space. Both are geometry
/// because undo has to put both back.
/// </summary>
public sealed record MagnifyGeometryState(Size Size, double Zoom, Point SourceCentre) : GeometryState;

/// <summary>
/// A step badge is one number wide — its diameter. What it <i>reads</i> is not here
/// and is not anywhere: the number is the object's position among the steps, so
/// there is nothing about it for undo to restore.
/// </summary>
public sealed record StepGeometryState(double Diameter) : GeometryState;

/// <summary>
/// Text plus where it points. <paramref name="Tail"/> is relative to the bubble, so
/// moving a callout carries its tail — unlike a magnifier's subject, which is
/// absolute because it belongs to the picture rather than to the object.
/// </summary>
public sealed record CalloutGeometryState(string Text, double FontSize, Vector Tail) : GeometryState;

/// <summary>Carried by an annotation this build cannot interpret, and never edited.</summary>
public sealed record OpaqueGeometryState : GeometryState;
