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

/// <summary>Carried by an annotation this build cannot interpret, and never edited.</summary>
public sealed record OpaqueGeometryState : GeometryState;
