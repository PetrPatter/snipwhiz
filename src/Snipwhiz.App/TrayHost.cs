using System.Drawing;
using System.Reflection;
using System.Windows.Forms;
using Snipwhiz.Core;

namespace Snipwhiz.App;

/// <summary>
/// Tray icon, menu, and notifications. Balloon tips rather than toasts:
/// toasts from an unpackaged app are silently dropped until the spec 3
/// installer creates a shortcut with a matching AppUserModelID.
/// </summary>
public sealed class TrayHost : IDisposable
{
    /// <summary>
    /// Read from the running assembly, never from a constant, so the About text
    /// cannot claim a version the binary is not. The informational version is the
    /// one that matches the installer's semver; it carries a <c>+commit</c> suffix
    /// when the SDK stamps one, which is for a build log rather than a menu.
    /// </summary>
    private static string Version =>
        typeof(TrayHost).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            .Split('+')[0]
        ?? "unknown";

    private readonly NotifyIcon _icon;
    private readonly ToolStripMenuItem _autostart;
    private readonly Settings _settings;
    private readonly string _root;

    /// <summary>
    /// Set by the first-run window, which offers the same choice this menu item
    /// does. Routed through the menu item rather than around it so the registry
    /// write keeps one owner and the tick stays honest about what is on disk.
    /// </summary>
    public bool Autostart
    {
        get => _autostart.Checked;
        set => _autostart.Checked = value;
    }

    public event Action? RegionRequested;
    public event Action? FullscreenRequested;
    public event Action? LibraryRequested;
    public event Action? CancelRequested;   // inert until Task 8 wires it to the overlay
    public event Action? ExitRequested;

    public TrayHost(Settings settings, string root)
    {
        _settings = settings;
        _root = root;

        var menu = new ContextMenuStrip();
        menu.Items.Add("Capture region\tCtrl+Shift+1", null, (_, _) => RegionRequested?.Invoke());
        menu.Items.Add("Capture screen\tCtrl+Shift+2", null, (_, _) => FullscreenRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Library\tCtrl+Shift+L", null, (_, _) => LibraryRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Cancel capture", null, (_, _) => CancelRequested?.Invoke());
        menu.Items.Add(new ToolStripSeparator());

        _autostart = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = true,
            Checked = _settings.Autostart,
        };
        _autostart.CheckedChanged += (_, _) => SetAutostart(_autostart.Checked);
        menu.Items.Add(_autostart);

        menu.Items.Add($"About Snipwhiz {Version}", null, (_, _) => MessageBox.Show(
            $"Snipwhiz {Version}", "About Snipwhiz", MessageBoxButtons.OK, MessageBoxIcon.Information));

        menu.Items.Add("Exit", null, (_, _) => ExitRequested?.Invoke());

        _icon = new NotifyIcon
        {
            Icon = SystemIcons.Application,       // replaced with real branding in spec 6
            Text = "Snipwhiz",
            Visible = true,
            ContextMenuStrip = menu,
        };
        // Opens the library, not a capture: a hotkey is the natural way to capture,
        // and a double-click is the natural way to open a window.
        _icon.DoubleClick += (_, _) => LibraryRequested?.Invoke();
    }

    public void ShowBalloon(string title, string text, bool isError = false)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = isError ? ToolTipIcon.Error : ToolTipIcon.Info;
        _icon.ShowBalloonTip(5000);
    }

    private void SetAutostart(bool enabled)
    {
        AutostartEntry.Set(enabled);

        _settings.Autostart = enabled;
        _settings.Save(_root);
    }

    public void Dispose()
    {
        _icon.Visible = false;
        _icon.Dispose();
    }
}
