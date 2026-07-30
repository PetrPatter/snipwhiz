using System.Windows;
using Xunit;

namespace Snipwhiz.App.Tests;

/// <summary>
/// The first-run window is mostly text, and text does not need a test. Two things
/// on it do.
///
/// <para>One is a consent invariant rather than a preference: autostart writes to
/// the registry, and this app's standing rule is that it never does so unasked. A
/// pre-ticked box turns a question into an announcement, and it would be a
/// one-character change to make.</para>
///
/// <para>The other is the PrintScreen offer, which is conditional. Offering to take
/// a key nothing is holding is a question with no meaning, and answering it would
/// release a binding the user never had.</para>
/// </summary>
public class FirstRunTests
{
    private static void Window(bool offerPrintScreen, Action<FirstRunWindow> body) => Sta.Run(() =>
    {
        // Off-screen rather than hidden, for the same reason Harness is: a hidden
        // window never arranges, and Visibility is what these assertions read.
        var window = new FirstRunWindow(offerPrintScreen)
        {
            Left = -10_000,
            Top = -10_000,
            ShowActivated = false,
            ShowInTaskbar = false,
        };

        window.Show();
        try
        {
            window.UpdateLayout();
            body(window);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public void Autostart_is_never_offered_pre_ticked() =>
        Window(offerPrintScreen: false, window => Assert.False(window.StartWithWindows));

    [Fact]
    public void The_printscreen_offer_is_hidden_when_nothing_holds_the_key() =>
        Window(offerPrintScreen: false, window =>
        {
            Assert.Equal(Visibility.Collapsed, window.PrintScreen.Visibility);
            Assert.False(window.TakeOverPrintScreen);
        });

    [Fact]
    public void The_printscreen_offer_appears_when_the_snipping_tool_holds_it() =>
        Window(offerPrintScreen: true, window =>
        {
            Assert.Equal(Visibility.Visible, window.PrintScreen.Visibility);
            // Visible is an offer, not an answer.
            Assert.False(window.TakeOverPrintScreen);
        });
}
