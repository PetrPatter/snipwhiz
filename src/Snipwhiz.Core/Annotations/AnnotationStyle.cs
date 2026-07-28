using System.Windows.Media;

namespace Snipwhiz.Core.Annotations;

/// <summary>
/// Every visual property any tool uses, on one record.
///
/// <para>One record rather than a style type per tool, because the contextual
/// toolbar has to reflect and edit whatever is selected without a type switch per
/// control, and because multi-select must be able to set "stroke red" across mixed
/// types. The cost is properties that are meaningless on some types; the
/// alternative is a visitor over fifteen style classes.</para>
///
/// <para>Holds <see cref="Color"/> values and numbers, never <see cref="Brush"/>:
/// brushes are mutable, thread-affine unless frozen, and awkward to serialize.
/// Rendering converts.</para>
///
/// <para>This grows as phases land â€” arrowhead shape, font, shadow presets, corner
/// radius. Phase A carries what a rectangle needs.</para>
/// </summary>
public sealed record AnnotationStyle
{
    /// <summary>The accent from the approved prototype.</summary>
    public static readonly AnnotationStyle Default = new();

    public Color Stroke { get; init; } = Color.FromRgb(0xE5, 0x48, 0x4D);

    public double StrokeWidth { get; init; } = 4;

    /// <summary>Null means unfilled â€” the shape shows what is underneath it.</summary>
    public Color? Fill { get; init; }

    public double Opacity { get; init; } = 1;
}
