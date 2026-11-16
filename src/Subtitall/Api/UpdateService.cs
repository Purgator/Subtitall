using ElectronNET.API;
using Subtitall.Core;

namespace Subtitall.Api;

/// <summary>What the auto-updater is doing right now, for the UI.</summary>
public sealed record UpdateStatus(string State, string? Version, double? Percent, string? Message, DateTimeOffset At)
{
    public static UpdateStatus Idle(string message) => new("idle", null, null, message, DateTimeOffset.UtcNow);
}

/// <summary>
/// Wraps electron-updater: relays its events to the UI (so an update never downloads behind the
/// user's back without a word) and serves "check for updates" and "restart to install".
/// States: idle, checking, upToDate, available, downloading, downloaded, installing, error, unsupported.
/// </summary>
public sealed class UpdateService(EventBus events, ILogger<UpdateService> logger)
{
    /// <summary>How long a check may stay without an answer before the UI is told so.</summary>
    private static readonly TimeSpan CheckTimeout = TimeSpan.FromSeconds(30);

    private readonly Lock _lock = new();
    private bool _wired;
    private int _check; // Which check is running — an old watchdog must not judge a newer check.
    private bool _updaterAnswered; // electron-updater said "checking" for the running check.
    private bool _installing;
    private UpdateStatus _status = UpdateStatus.Idle("Not checked yet");

    public UpdateStatus Status
    {
        get { lock (_lock) return _status; }
    }

    /// <summary>Hooks the updater events once; safe to call before the first check.</summary>
    public void Wire()
    {
        lock (_lock)
        {
            if (_wired)
                return;
            _wired = true;
        }
        if (!ElectronBridge.IsActive)
        {
            Set(new UpdateStatus("unsupported", null, null, "Updates only work in the installed desktop app", DateTimeOffset.UtcNow));
            return;
        }
        try
        {
            Electron.AutoUpdater.OnCheckingForUpdate += MarkUpdaterStarted;
            Electron.AutoUpdater.OnUpdateNotAvailable += info =>
                Set(new UpdateStatus("upToDate", info?.Version, null, $"You're on the latest version (v{UserAgent.Version})", DateTimeOffset.UtcNow));
            Electron.AutoUpdater.OnUpdateAvailable += info =>
                Set(new UpdateStatus("available", info?.Version, 0, $"Update v{info?.Version} found — downloading", DateTimeOffset.UtcNow));
            Electron.AutoUpdater.OnDownloadProgress += progress =>
                Set(new UpdateStatus("downloading", Status.Version, Math.Round(progress.Percent, 1), $"Downloading update… {progress.Percent:0}%", DateTimeOffset.UtcNow));
            Electron.AutoUpdater.OnUpdateDownloaded += info =>
            {
                logger.LogInformation("Update {Version} downloaded", info?.Version);
                Set(new UpdateStatus("downloaded", info?.Version, 100, $"Update v{info?.Version} is ready — it installs when you restart", DateTimeOffset.UtcNow));
            };
            Electron.AutoUpdater.OnError += message =>
            {
                logger.LogWarning("Auto-updater: {Message}", message);
                Set(new UpdateStatus("error", Status.Version, null, Friendly(message), DateTimeOffset.UtcNow));
            };
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not hook the auto-updater");
        }
    }

    /// <summary>Asks GitHub Releases for a newer version; the outcome arrives through the events.</summary>
    public async Task CheckAsync()
    {
        Wire();
        if (Status.State is "unsupported" or "checking" or "downloading")
            return;
        var check = BeginCheck();

        // A check that never answers must not spin forever: in an unpackaged dev run
        // electron-updater silently no-ops (the call may not even return), and in the
        // installed app a request can hang or an event can get lost on the way.
        _ = Task.Run(async () =>
        {
            await Task.Delay(CheckTimeout);
            ExpireCheck(check);
        });

        try
        {
            // Downloads in the background and installs when the app quits
            // (autoInstallOnAppQuit is electron-updater's default).
            var result = await Electron.AutoUpdater.CheckForUpdatesAndNotifyAsync();

            // The outcome normally arrives through the events. If the call is back and none
            // did, its own result still says whether there is something newer.
            await Task.Delay(TimeSpan.FromSeconds(3));
            CompleteCheck(check, result?.UpdateInfo?.Version);
        }
        catch (Exception ex)
        {
            // Never let update plumbing break the app (e.g. offline).
            logger.LogWarning(ex, "Update check failed");
            Set(new UpdateStatus("error", null, null, Friendly(ex.Message), DateTimeOffset.UtcNow));
        }
    }

    /// <summary>Starts a check: the status says "checking" until an answer or the timeout.</summary>
    internal int BeginCheck()
    {
        int check;
        lock (_lock)
        {
            check = ++_check;
            _updaterAnswered = false;
        }
        Set(new UpdateStatus("checking", null, null, "Checking for updates…", DateTimeOffset.UtcNow));
        return check;
    }

    /// <summary>electron-updater picked the check up — so updates do work in this build.</summary>
    internal void MarkUpdaterStarted()
    {
        lock (_lock) _updaterAnswered = true;
    }

    /// <summary>
    /// The check call returned: if no event settled the status meanwhile, settle it from the
    /// version the call reported (the same version as ours means there is nothing newer).
    /// </summary>
    internal void CompleteCheck(int check, string? latestVersion)
    {
        if (!IsStillChecking(check) || latestVersion is null)
            return;
        if (latestVersion.TrimStart('v') == UserAgent.Version)
            Set(new UpdateStatus("upToDate", latestVersion, null, $"You're on the latest version (v{UserAgent.Version})", DateTimeOffset.UtcNow));
    }

    /// <summary>Ends a check that got no answer in time, saying why instead of spinning on.</summary>
    internal void ExpireCheck(int check)
    {
        if (!IsStillChecking(check))
            return;
        bool answered;
        lock (_lock) answered = _updaterAnswered;
        logger.LogWarning("Update check {Check} got no answer (updater started: {Started})", check, answered);
        Set(answered
            ? new UpdateStatus("error", null, null, "The update check got no answer — try again later", DateTimeOffset.UtcNow)
            : new UpdateStatus("unsupported", null, null, "Update check didn't answer — updates only work in the installed app", DateTimeOffset.UtcNow));
    }

    private bool IsStillChecking(int check)
    {
        lock (_lock) return _check == check && _status.State == "checking";
    }

    /// <summary>
    /// Installs the downloaded update and restarts the app. Silently: the installer runs without
    /// its wizard (asking where to install again makes no sense for an update, and the wizard
    /// relaunching itself looked like two installs) and reopens the app when done.
    /// </summary>
    public bool Install() => BeginInstall() && RunInstall();

    /// <summary>Only a downloaded update can be installed, and only once — a second click must not start a second installer.</summary>
    internal bool BeginInstall()
    {
        string? version;
        lock (_lock)
        {
            if (_installing || _status.State != "downloaded")
                return false;
            _installing = true;
            version = _status.Version;
        }
        Set(new UpdateStatus("installing", version, 100, "Installing the update — Subtitall restarts by itself", DateTimeOffset.UtcNow));
        return true;
    }

    private bool RunInstall()
    {
        _ = Task.Run(async () =>
        {
            try
            {
                // The app is about to be closed under the user's feet: take its windows away
                // first, so there is nothing half-dead left to click on in the meantime.
                foreach (var window in Electron.WindowManager.BrowserWindows.ToList())
                    window.Hide();

                Electron.AutoUpdater.QuitAndInstall(isSilent: true, isForceRunAfter: true);

                // quitAndInstall asks Electron to quit, which has been seen to hang with the
                // window still up. The installer is running by now — make sure we are gone.
                await Task.Delay(TimeSpan.FromSeconds(4));
                Electron.App.Exit(0);
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Could not start the update installer");
                foreach (var window in Electron.WindowManager.BrowserWindows.ToList())
                    window.Show();
                AbortInstall("Could not start the update installer: " + ex.Message);
            }
        });
        return true;
    }

    /// <summary>The installer did not start: give the app back and let the user try again.</summary>
    internal void AbortInstall(string message)
    {
        lock (_lock) _installing = false;
        Set(new UpdateStatus("error", null, null, message, DateTimeOffset.UtcNow));
    }

    private void Set(UpdateStatus status)
    {
        lock (_lock) _status = status;
        events.Publish("update-status", status);
    }

    private static string Friendly(string message) =>
        message.Contains("ENOTFOUND", StringComparison.OrdinalIgnoreCase) || message.Contains("net::", StringComparison.OrdinalIgnoreCase)
            ? "Could not reach GitHub to check for updates (offline?)"
            : "Update check failed: " + message;
}
