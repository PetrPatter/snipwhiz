using System.Windows.Controls.Primitives;
using System.Windows.Input;
using Snipwhiz.App.Editor;

namespace Snipwhiz.App.Tests;

/// <summary>
/// The rail must show which tool is active, whether it was chosen by mouse or by
/// keyboard.
///
/// <para><b>This is a regression suite before it is anything else.</b> The rail
/// carried a hand-written list of its own buttons, and that list was missing arrow
/// and highlight — so those two never lit from a shortcut and, worse, never
/// <i>cleared</i> when another tool was picked, leaving two buttons looking active
/// at once. Clicking hid the whole thing, because a <see cref="ToggleButton"/> flips
/// its own <c>IsChecked</c> and so appeared to work while the code driving it did
/// nothing.</para>
///
/// <para>Which is why these assert <b>exactly one</b> button is checked rather than
/// that the expected one is. "The right button lit" was true throughout the bug.</para>
/// </summary>
public class ToolRailTests
{
    /// <summary>Every bare-letter tool shortcut, and the button it must light.</summary>
    public static TheoryData<Key, string> Shortcuts => new()
    {
        { Key.V, "SelectToolButton" },
        { Key.R, "RectToolButton" },
        { Key.E, "EllipseToolButton" },
        { Key.L, "LineToolButton" },
        { Key.A, "ArrowToolButton" },
        { Key.H, "HighlightToolButton" },
        { Key.T, "TextToolButton" },
        { Key.O, "CalloutToolButton" },
        { Key.N, "StepToolButton" },
        { Key.M, "MagnifyToolButton" },
        { Key.S, "SpotlightToolButton" },
        { Key.B, "BlurToolButton" },
        { Key.P, "PixelateToolButton" },
        { Key.C, "CropToolButton" },
    };

    /// <summary>
    /// The list above is hand-written, which is the hazard this whole file exists to
    /// catch — so it is checked against the rail rather than trusted. A tool added to
    /// the rail without a case here would otherwise be exactly the untested button
    /// arrow and highlight were.
    /// </summary>
    [Fact]
    public void Every_button_on_the_rail_has_a_shortcut_covered_here()
    {
        Harness.Editor(editor =>
        {
            var onRail = editor.ToolRail.Children.OfType<ToggleButton>().Select(b => b.Name);
            var covered = Shortcuts.Select(row => (string)row[1]!);

            Assert.Equal([.. onRail.Order()], [.. covered.Order()]);
        });
    }

    [Theory]
    [MemberData(nameof(Shortcuts))]
    public void A_shortcut_lights_its_own_button_and_only_its_own(Key key, string expected)
    {
        Harness.Editor(editor =>
        {
            Press(editor, key);

            var lit = Lit(editor);
            Assert.Single(lit);
            Assert.Equal(expected, lit[0]);
        });
    }

    [Fact]
    public void Switching_tools_by_keyboard_never_leaves_two_buttons_lit()
    {
        Harness.Editor(editor =>
        {
            // Arrow then highlight: the exact pair the missing list entries hid.
            foreach (var key in new[] { Key.A, Key.H, Key.R, Key.C, Key.V })
            {
                Press(editor, key);
                Assert.Single(Lit(editor));
            }
        });
    }

    /// <summary>
    /// A tool chosen by mouse must go dark when the next one is chosen by keyboard.
    ///
    /// <para>This is the half a click cannot show you. A <see cref="ToggleButton"/>
    /// lights itself on click whether or not the code driving the rail knows the
    /// button exists, so the bug was invisible until something else was picked
    /// afterwards and the first button stayed lit.</para>
    /// </summary>
    [Fact]
    public void A_tool_clicked_with_the_mouse_goes_dark_when_the_keyboard_picks_another()
    {
        Harness.Editor(editor =>
        {
            // Both halves of a real click, in order. ToggleButton.OnClick flips
            // IsChecked itself and only then raises Click; raising the routed event
            // alone leaves the button dark and quietly models nothing.
            editor.HighlightToolButton.IsChecked = true;
            editor.HighlightToolButton.RaiseEvent(
                new System.Windows.RoutedEventArgs(ButtonBase.ClickEvent));

            Press(editor, Key.V);

            Assert.Equal(["SelectToolButton"], Lit(editor));
        });
    }

    /// <summary>
    /// The rail is taller than a short window, and the tools past the fold have to
    /// remain reachable.
    ///
    /// <para>Before the rail sized to its content it stretched to the window and
    /// simply <b>clipped</b>: at the window's own <c>MinHeight</c> of 420, Crop and
    /// Pixelate were drawn past the bottom edge with no way to scroll to them. They
    /// still had shortcuts, so nothing failed — the buttons just were not there.</para>
    ///
    /// <para>The first two assertions are the control. If Crop were already on screen
    /// unscrolled, the third would pass without the scroller doing anything.</para>
    /// </summary>
    [Fact]
    public void Tools_past_the_bottom_of_a_short_window_can_still_be_scrolled_to()
    {
        Harness.Editor(editor =>
        {
            // The harness window is 300 tall and the rail wants roughly twice that.
            var scroller = editor.ToolRailScroller;
            Assert.True(scroller.ScrollableHeight > 0,
                $"the rail fits in {scroller.ViewportHeight:F0}px, so this proves nothing");

            Assert.True(BottomOf(editor.CropToolButton, scroller) > scroller.ViewportHeight,
                "Crop was already visible unscrolled, so the scroll below is not what makes it reachable");

            scroller.ScrollToEnd();
            editor.UpdateLayout();

            Assert.True(BottomOf(editor.CropToolButton, scroller) <= scroller.ViewportHeight + 1,
                "the last tool on the rail cannot be reached by scrolling");
        });
    }

    /// <summary>How far down the scroller's viewport an element's bottom edge sits.</summary>
    private static double BottomOf(System.Windows.FrameworkElement element, System.Windows.FrameworkElement scroller) =>
        element.TransformToAncestor(scroller)
            .TransformBounds(new System.Windows.Rect(element.RenderSize))
            .Bottom;

    // NOT COVERED: HandleKey returning false while a caption is being typed, so
    // that "T" is a letter and not the text tool. Reaching that state needs a real
    // drag on the canvas to create the object, which is a mouse-gesture harness
    // these four tests do not need. Recorded as a gap rather than approximated by a
    // test that only asserts its own precondition.

    private static void Press(EditorView editor, Key key)
    {
        var handled = editor.HandleKey(new KeyEventArgs(
            Keyboard.PrimaryDevice, Harness.Source(editor), 0, key)
        {
            RoutedEvent = Keyboard.KeyDownEvent,
        });

        Assert.True(handled, $"{key} was not handled at all.");
    }

    private static string[] Lit(EditorView editor) =>
        [.. editor.ToolRail.Children.OfType<ToggleButton>()
            .Where(b => b.IsChecked == true)
            .Select(b => b.Name)];
}
