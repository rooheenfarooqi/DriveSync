using System.Reflection;
using System.Windows;
using System.Windows.Threading;
using DriveSync.Core.Indexing;
using Velopack;
using Velopack.Sources;

namespace DriveSync.App.Shell;

public enum UpdateState { Unavailable, Idle, Checking, Downloading, Ready, UpToDate, Failed }

/// <summary>
/// Updates from the project's GitHub Releases, through Velopack. Only an installed copy updates: runs from
/// source, on a test database or in the screenshot tour never do. Velopack downloads over HTTPS and checks
/// each package's SHA-256 against the release feed before applying it.
/// </summary>
public static class Updater
{
    public const string RepoUrl = "https://github.com/rooheenfarooqi/DriveSync";
    private static readonly TimeSpan FirstCheck = TimeSpan.FromSeconds(45);
    private static readonly TimeSpan Every = TimeSpan.FromHours(6);

    private static UpdateManager? _manager;
    private static UpdateInfo? _ready;
    private static DispatcherTimer? _timer;
    private static bool _busy, _applying;

    public static UpdateState State { get; private set; } = UpdateState.Unavailable;
    public static string Detail { get; private set; } = "Updates work in the installed app.";
    public static event Action? Changed;

    /// <summary>The version being run, such as 0.9.0-beta.1 (without the build's commit hash).</summary>
    public static string Version { get; } =
        (typeof(Updater).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.9.0").Split('+')[0];

    public static bool IsInstalled => _manager?.IsInstalled == true;

    /// <summary>Called once the index has loaded, so the first check never competes with start-up.</summary>
    public static void Start()
    {
        if (App.SandboxFolder != null || ScreenshotTour.Active) return;
        try
        {
            // A beta build also takes newer betas; a stable build only takes stable releases.
            _manager = new UpdateManager(new GithubSource(RepoUrl, accessToken: null, prerelease: Version.Contains('-')));
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _manager = null;
        }
        if (!IsInstalled) return;

        Set(UpdateState.Idle, "Not checked yet.");
        _timer = new DispatcherTimer { Interval = FirstCheck };
        _timer.Tick += async (_, _) =>
        {
            _timer.Interval = Every;
            if (App.Service.Settings.AutoUpdate) await CheckAsync(manual: false);
        };
        _timer.Start();
    }

    /// <summary>Checks GitHub and downloads a newer version in the background. Manual checks always report back.</summary>
    public static async Task CheckAsync(bool manual)
    {
        if (_manager is not { IsInstalled: true } manager)
        {
            if (manual) Ui.Toast(NoticeKind.Info, "Updates work in the installed app", "Download DriveSync from its GitHub Releases page to get updates.");
            return;
        }
        if (_ready != null)
        {
            if (manual) OfferRestart();
            return;
        }
        if (_busy) return;
        _busy = true;
        Set(UpdateState.Checking, "Checking GitHub for a new version…");
        try
        {
            UpdateInfo? info = await manager.CheckForUpdatesAsync();
            if (info == null)
            {
                Set(UpdateState.UpToDate, $"Up to date · checked {DateTime.Now:t}");
                if (manual) Ui.Toast(NoticeKind.Ok, "DriveSync is up to date", $"Version {Version} is the latest.", ms: 2800);
                return;
            }
            string next = info.TargetFullRelease.Version.ToString();
            Set(UpdateState.Downloading, $"Downloading version {next}…");
            Dispatcher ui = Application.Current.Dispatcher;
            await manager.DownloadUpdatesAsync(info, p => ui.BeginInvoke(() => Set(UpdateState.Downloading, $"Downloading version {next}… {p}%")));
            _ready = info;
            Set(UpdateState.Ready, $"Version {next} is ready. It installs when DriveSync restarts.");
            OfferRestart();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            Set(UpdateState.Failed, "Couldn't check for updates. DriveSync will try again later.");
            if (manual) Ui.Toast(NoticeKind.Err, "Couldn't check for updates", ex.Message);
        }
        finally
        {
            _busy = false;
        }
    }

    private static void OfferRestart() =>
        Ui.Toast(NoticeKind.Ok, $"DriveSync {_ready!.TargetFullRelease.Version} is ready", "Restart to finish updating.", "Restart now", RestartNow, ms: 12000);

    /// <summary>Saves the index, quits, installs the downloaded version and starts it again.</summary>
    public static void RestartNow()
    {
        if (_manager == null || _ready == null || _applying) return;
        _applying = true;
        string[]? args = App.Host is { IsVisible: true } ? null : new[] { "--background" };
        _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: true, restartArgs: args);
        App.Quit();
    }

    /// <summary>Quitting with an update downloaded installs it quietly, ready for next time.</summary>
    public static void ApplyOnExit()
    {
        if (_manager == null || _ready == null || _applying) return;
        _applying = true;
        try { _manager.WaitExitThenApplyUpdates(_ready.TargetFullRelease, silent: true, restart: false); }
        catch (Exception ex) when (ex is not OutOfMemoryException) { /* The update applies on the next restart instead. */ }
    }

    private static void Set(UpdateState state, string detail)
    {
        State = state;
        Detail = detail;
        Changed?.Invoke();
    }
}
