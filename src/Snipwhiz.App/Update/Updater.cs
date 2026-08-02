using Velopack;
using Velopack.Sources;

namespace Snipwhiz.App.Update;

/// <summary>
/// Checks GitHub Releases once per launch, downloads anything newer in the
/// background, and applies it when the app exits — never while it is running.
///
/// <para><b>Applying mid-session is the thing this must not do.</b> Swapping files
/// under a running app is how an update takes a screenshot away from someone
/// half-way through annotating it. Downloading is safe at any time; installing waits
/// for the process to end, so the next launch is simply the new version.</para>
///
/// <para><b>Every failure is silent.</b> No network, a rate-limited GitHub, a
/// corporate proxy — all ordinary, and none of them worth a dialog. An app that
/// cannot reach its update feed is an app that works exactly as it did yesterday,
/// and saying so out loud tells the user about a problem they cannot fix and do not
/// have. Spec §4.3.</para>
/// </summary>
internal sealed class Updater
{
    /// <summary>
    /// The feed. GitHub Releases on the public repository, so no token is needed and
    /// none is embedded — unauthenticated requests are rate-limited per IP at 60 an
    /// hour, and this asks once per launch.
    /// </summary>
    private const string Repository = "https://github.com/PetrPatter/snipwhiz";

    private UpdateManager? _manager;
    private UpdateInfo? _downloaded;

    /// <summary>
    /// An update is on disk and will be applied when this process ends. The one
    /// thing the user is ever told, and only as an affordance they can ignore.
    /// </summary>
    public bool RestartPending => _downloaded is not null;

    /// <summary>Raised on a background thread when <see cref="RestartPending"/> becomes true.</summary>
    public event Action? Ready;

    /// <summary>
    /// Looks for a newer release and fetches it.
    ///
    /// <para>Does nothing at all when the app is not installed — running from a build
    /// output or a portable copy has no install to update, and Velopack throws rather
    /// than returning empty if asked anyway.</para>
    /// </summary>
    public async Task CheckAsync()
    {
        try
        {
            var manager = new UpdateManager(new GithubSource(Repository, null, false));
            if (!manager.IsInstalled) return;

            _manager = manager;

            var update = await manager.CheckForUpdatesAsync().ConfigureAwait(false);
            if (update is null) return;

            await manager.DownloadUpdatesAsync(update).ConfigureAwait(false);

            _downloaded = update;
            Ready?.Invoke();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Deliberately everything. This runs unattended on someone else's
            // machine and there is no failure here that should reach them: the worst
            // case is that they keep running the version they already have.
        }
    }

    /// <summary>
    /// Hands the downloaded update to Velopack's updater, which waits for this
    /// process to end and then installs it.
    ///
    /// <para>Call while shutting down, before the process actually goes. The updater
    /// gives up after 60 seconds of waiting, so an app that hangs on exit simply does
    /// not update — which is the right way round.</para>
    /// </summary>
    /// <param name="restart">
    /// True only when the user asked for it. On an ordinary exit the app must stay
    /// exited: relaunching a tray app someone has just closed is not an update, it is
    /// an argument.
    /// </param>
    public void Apply(bool restart)
    {
        if (_manager is null || _downloaded is null) return;

        try
        {
            _manager.WaitExitThenApplyUpdates(_downloaded.TargetFullRelease, silent: true, restart: restart);
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Same reasoning as the check. A failure to start the updater leaves the
            // current version in place and working.
        }
    }
}
