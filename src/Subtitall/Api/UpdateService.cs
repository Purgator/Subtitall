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
/// States: idle, checking, upToDate, available, downloading, downloaded, error, unsupported.
/// </summary>
public sealed class UpdateService(EventBus events, ILogger<UpdateService> logger)
{
    private readonly Lock _lock = new();
    private bool _wired;
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
            Electron.AutoUpdater.OnCheckingForUpdate += () =>
                Set(new UpdateStatus("checking", null, null, "Checking for updates…", DateTimeOffset.UtcNow));
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
        var started = DateTimeOffset.UtcNow;
        Set(new UpdateStatus("checking", null, null, "Checking for updates…", started));

        // In an unpackaged dev run electron-updater silently no-ops (the call may not even
        // return): say so instead of spinning forever.
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20));
            if (Status is { State: "checking", At: var at } && at == started)
                Set(new UpdateStatus("unsupported", null, null, "Update check didn't answer — updates only work in the installed app", DateTimeOffset.UtcNow));
        });

        try
        {
            // Downloads in the background and installs when the app quits
            // (autoInstallOnAppQuit is electron-updater's default).
            await Electron.AutoUpdater.CheckForUpdatesAndNotifyAsync();
        }
        catch (Exception ex)
        {
            // Never let update plumbing break the app (e.g. offline).
            logger.LogWarning(ex, "Update check failed");
            Set(new UpdateStatus("error", null, null, Friendly(ex.Message), DateTimeOffset.UtcNow));
        }
    }

    /// <summary>Quits and installs the downloaded update.</summary>
    public bool Install()
    {
        if (Status.State != "downloaded")
            return false;
        Electron.AutoUpdater.QuitAndInstall(false, true);
        return true;
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
