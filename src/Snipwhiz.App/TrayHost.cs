using System.Drawing;
using System.IO;
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
    private readonly ToolStripMenuItem _restartForUpdate;
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

    /// <summary>
    /// Reveals the restart affordance. Idempotent, and safe from any thread — the
    /// update check runs off the UI thread and this is what it calls into.
    /// </summary>
    public void ShowUpdateReady()
    {
        if (_icon.ContextMenuStrip is { } menu && menu.InvokeRequired)
        {
            menu.BeginInvoke(ShowUpdateReady);
            return;
        }

        _restartForUpdate.Visible = true;
    }

    public event Action? RegionRequested;
    public event Action? FullscreenRequested;
    public event Action? LibraryRequested;
    public event Action? RestartForUpdateRequested;
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

        // The entire update interface. Hidden until there is something to restart
        // for, so on an up-to-date machine this menu looks exactly as it always has.
        // No changelog, no "what's new", no badge, no nagging: spec section 4.3.
        _restartForUpdate = new ToolStripMenuItem("Restart to finish updating")
        {
            Visible = false,
        };
        _restartForUpdate.Click += (_, _) => RestartForUpdateRequested?.Invoke();
        menu.Items.Add(_restartForUpdate);
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
            Icon = TrayIcon(),
            Text = "Snipwhiz",
            Visible = true,
            ContextMenuStrip = menu,
        };
        // Opens the library, not a capture: a hotkey is the natural way to capture,
        // and a click is the natural way to open a window.
        //
        // A *single* click, and that is the whole point. This was DoubleClick, which
        // works on the taskbar's own tray but not from the hidden-icons flyout — the
        // flyout closes on the first click, so the second one lands on whatever is
        // underneath it and the icon never sees a double-click at all. For an app
        // whose icon lives in the overflow by default, that meant clicking it
        // appeared to do nothing.
        //
        // ShowLibrary reveals an existing window rather than making a second one, so
        // a double-click firing this twice is harmless.
        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == MouseButtons.Left) LibraryRequested?.Invoke();
        };
    }

    /// <summary>
    /// The tray icon at the size Windows is actually asking for.
    ///
    /// <para>The .ico carries nine frames, and which one is picked matters more
    /// here than anywhere else in the app: the tray is the smallest surface the
    /// icon appears on, and the 16px frame is drawn with its own geometry rather
    /// than being a shrunk 256. Passing <see cref="SystemInformation.SmallIconSize"/>
    /// also gets this right on a scaled display, where "small" is 20 or 24 rather
    /// than 16.</para>
    ///
    /// <para>Falls back rather than throwing. A missing icon is a cosmetic fault;
    /// an exception here would take the tray, and therefore the whole app, down
    /// with it.</para>
    /// </summary>
    private static Icon TrayIcon()
    {
        try
        {
            var resource = System.Windows.Application.GetResourceStream(
                new Uri("pack://application:,,,/Snipwhiz.ico", UriKind.Absolute));
            if (resource is null) return SystemIcons.Application;

            using var stream = resource.Stream;
            return new Icon(stream, SystemInformation.SmallIconSize);
        }
        catch (Exception e) when (e is IOException or ArgumentException or UriFormatException)
        {
            return SystemIcons.Application;
        }
    }

    /// <summary>
    /// Says something in a notification.
    /// </summary>
    ///
    /// <remarks>
    /// <para><b>Ordinary notifications carry no glyph.</b> <c>ToolTipIcon.Info</c> put
    /// a large generic blue "i" in the toast — Windows' icon, announcing Windows,
    /// where this app's own identity should be. <c>None</c> leaves the header's app
    /// icon as the only mark on it, which is the right one.</para>
    ///
    /// <para>A custom image in that slot is not reachable from here: it needs the
    /// <c>NIIF_USER</c> flag on <c>NOTIFYICONDATA</c>, which <see cref="NotifyIcon"/>
    /// does not expose and cannot be set behind its back without owning the tray icon
    /// at the Win32 level.</para>
    ///
    /// <para>Errors keep their glyph. The red cross is not standing in for a logo —
    /// it is the one piece of information a notification can carry before it is read.</para>
    /// </remarks>
    public void ShowBalloon(string title, string text, bool isError = false)
    {
        _icon.BalloonTipTitle = title;
        _icon.BalloonTipText = text;
        _icon.BalloonTipIcon = isError ? ToolTipIcon.Error : ToolTipIcon.None;
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
