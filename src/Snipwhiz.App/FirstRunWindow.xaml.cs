using System.Windows;

namespace Snipwhiz.App;

/// <summary>
/// What a person sees between double-clicking Setup.exe and taking their first
/// screenshot.
///
/// <para>One window rather than a wizard, and it absorbed a question rather than
/// adding one. A first launch already showed a "Snipwhiz is running" balloon and,
/// separately, a PrintScreen message box; a third interruption would have been a
/// wizard assembled by accident. The PrintScreen offer moved in here, so first run
/// is now this window and nothing else.</para>
///
/// <para>It decides nothing itself. The caller reads the two properties and applies
/// them through the paths that already exist, because autostart writes to the
/// registry and that write has one owner.</para>
/// </summary>
public partial class FirstRunWindow : Window
{
    public FirstRunWindow(bool offerPrintScreen)
    {
        InitializeComponent();

        if (offerPrintScreen) PrintScreen.Visibility = Visibility.Visible;
    }

    /// <summary>Unticked by default: autostart is consent, not a default.</summary>
    public bool StartWithWindows => Autostart.IsChecked == true;

    public bool TakeOverPrintScreen => PrintScreen.IsChecked == true;

    private void Dismiss(object sender, RoutedEventArgs e) => Close();
}
