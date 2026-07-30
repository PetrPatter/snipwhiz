using Velopack;

namespace Snipwhiz.App;

/// <summary>
/// An explicit entry point, which this app did not have until packaging needed one.
///
/// <para>Velopack does not run its install and uninstall work in a separate
/// installer process. It re-invokes <b>this exe</b> with arguments like
/// <c>--veloapp-install</c>, lets the app act on them, and expects it to exit.
/// <see cref="VelopackApp.Run"/> is what recognises those arguments, and it has to
/// come before anything else runs.</para>
///
/// <para>Before anything else is not a style preference here. <see cref="App"/>
/// takes a single-instance mutex, puts a tray icon on screen and registers global
/// hotkeys. Removing the call and passing <c>--veloapp-install</c> was tried: the
/// process was still running eight seconds later with a tray icon on screen. So
/// without this line every install and every update briefly launches a real,
/// visible Snipwhiz whose only job was to exit, and it takes the single-instance
/// mutex from the copy the user is actually running.</para>
/// </summary>
internal static class Program
{
    [STAThread]
    public static void Main()
    {
        VelopackApp.Build().Run();

        var app = new App();
        app.InitializeComponent();
        app.Run();
    }
}
