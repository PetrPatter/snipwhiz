using System.Windows;
using System.Windows.Controls;
using System.Windows.Markup;
using System.Windows.Media;
using Snipwhiz.Core.Annotations;

namespace Snipwhiz.App.Editor;

/// <summary>
/// Where the editing <c>TextBox</c> goes so that its glyphs land on the same pixels
/// the annotation would have drawn them on.
///
/// <para>Pure, and separate from the overlay, so the seam gate can position a
/// <c>TextBox</c> with the <b>same</b> code the editor uses. A gate that positioned
/// its own copy would prove the gate agrees with itself.</para>
/// </summary>
internal static class TextOverlayPlacement
{
    /// <summary>
    /// Maps the box's own coordinates to element coordinates on the canvas.
    ///
    /// <para>Three steps, in this order: shift so the box's origin sits where the
    /// annotation's first glyph starts, apply the annotation's own rotation and
    /// position, then apply the view. The last two are exactly what
    /// <c>CanvasHost.ToElement</c> does to any other point, which is what keeps the
    /// box glued to the object through zoom, pan and rotation.</para>
    /// </summary>
    public static Matrix For(TextAnnotation target, double zoom, Vector pan, Vector contentInset)
    {
        // To the first glyph, not to the plate's corner: TextOrigin is where DrawText
        // puts the annotation's first character, and the inset cancels wherever the
        // TextBox decided to put its own.
        var origin = target.TextOrigin;

        var m = Matrix.Identity;
        m.Translate(-contentInset.X, -contentInset.Y);
        m.Translate(origin.X, origin.Y);
        m.Append(target.Transform);
        m.Scale(zoom, zoom);
        m.Translate(pan.X, pan.Y);
        return m;
    }

    /// <summary>
    /// Where the box actually put its first character, in its own coordinates.
    ///
    /// <para><b>Asked, not assumed.</b> The seam gate measured a flat two-pixel
    /// horizontal shift, and stripping the control template down to nothing but its
    /// content host did not move it — so the inset is in <c>TextBoxView</c>'s own
    /// layout rather than in any template this code can see. Hardcoding two would
    /// fix today's number with a constant nobody here chose, out of an
    /// implementation detail that is free to differ by theme, by font and by Windows
    /// version. WPF already knows the answer, so it is asked.</para>
    ///
    /// <para>Requires the box to have been laid out; callers go through
    /// <see cref="Apply"/>, which does that first.</para>
    /// </summary>
    private static Vector ContentInset(TextBox box)
    {
        var first = box.GetRectFromCharacterIndex(0);

        // Empty or not yet laid out gives an infinite rect rather than throwing.
        return double.IsInfinity(first.X) || double.IsInfinity(first.Y)
            ? default
            : new Vector(first.X, first.Y);
    }

    /// <summary>
    /// Styles, measures and positions a box over an annotation — the whole seam in
    /// one call, so the editor and the gate cannot drift apart by doing two of the
    /// three steps in a different order.
    /// </summary>
    public static Vector Apply(TextBox box, TextAnnotation target, double zoom, Vector pan)
    {
        Match(box, target);
        box.UpdateLayout();

        var inset = ContentInset(box);
        box.RenderTransform = new MatrixTransform(For(target, zoom, pan, inset));
        return inset;
    }

    /// <summary>
    /// A template with nothing in it but the content host.
    ///
    /// <para>The stock <c>TextBox</c> template insets its content, and the seam gate
    /// measured that as a flat two-pixel shift at both DPIs — the words moved
    /// sideways the moment you clicked into them. Subtracting two from the padding
    /// would fix the number by encoding a constant nobody here chose, out of a
    /// template that can differ by theme and by Windows version. Owning the template
    /// removes the inset instead of compensating for it.</para>
    /// </summary>
    private static readonly ControlTemplate BareTemplate = (ControlTemplate)XamlReader.Parse(
        """
        <ControlTemplate xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                         xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                         TargetType="TextBox">
          <ScrollViewer x:Name="PART_ContentHost" Focusable="False"
                        Margin="0" Padding="0" BorderThickness="0"
                        HorizontalScrollBarVisibility="Hidden"
                        VerticalScrollBarVisibility="Hidden"/>
        </ControlTemplate>
        """);

    /// <summary>
    /// Applies the annotation's font, padding and formatting mode to a box.
    ///
    /// <para>Every one of these has to match what <see cref="TextAnnotation.Format"/>
    /// does, and the formatting mode is the one that is invisible when wrong: with
    /// Display mode the box lays glyphs out on whole pixels and the annotation does
    /// not, so the text shifts the instant editing ends.</para>
    /// </summary>
    public static void Match(TextBox box, TextAnnotation target)
    {
        box.Template = BareTemplate;

        box.FontFamily = TextAnnotation.Face.FontFamily;
        box.FontStyle = TextAnnotation.Face.Style;
        box.FontWeight = TextAnnotation.Face.Weight;
        box.FontStretch = TextAnnotation.Face.Stretch;
        box.FontSize = target.FontSize;

        // No padding, and sized to the words alone. The plate's padding is carried by
        // the placement matrix instead, so there is one thing deciding where the text
        // sits rather than a padding here and an offset there to keep in agreement.
        box.Padding = new Thickness(0);
        box.BorderThickness = new Thickness(0);
        box.Width = target.TextSize.Width;
        box.Height = target.TextSize.Height;

        TextOptions.SetTextFormattingMode(box, TextAnnotation.Formatting);

        var ink = new SolidColorBrush(target.Style.Stroke) { Opacity = target.Style.Opacity };
        ink.Freeze();
        box.Foreground = ink;
    }
}
