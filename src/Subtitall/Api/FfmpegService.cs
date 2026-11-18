using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using Subtitall.Core;
using Subtitall.Core.Reencode;

namespace Subtitall.Api;

/// <summary>An ffmpeg that was found and runs.</summary>
public sealed record FfmpegTools(string Ffmpeg, string Ffprobe, FfmpegSource Source, string Version, IReadOnlyList<string> Encoders);

/// <summary>
/// What the UI is told about ffmpeg. States: ready, missing, downloading, installing, error.
/// <see cref="Blocked"/>: an ffmpeg is there but the system refuses to start it.
/// <see cref="Fallbacks"/>: the package managers an install falls back on here.
/// <see cref="Tried"/>: what the last install attempted, and why each way failed.
/// </summary>
public sealed record FfmpegStatus(
    string State, string Os, bool CanDownload, FfmpegSource? Source, string? Version, string? Path,
    IReadOnlyList<string> Encoders, double? Percent, long DownloadedBytes, long TotalBytes, string? Message,
    string InstallFolder, bool Blocked, bool HasDownload, IReadOnlyList<string> Fallbacks, IReadOnlyList<string> Tried);

/// <summary>
/// Finds the ffmpeg the Re-encode tab works with, and installs one when the user asks for it:
/// first a download into the app's own folder (Windows and Linux: the BtbN builds; macOS: the
/// evermeet.cx builds), then — when that download cannot be used, typically because a security
/// policy refuses to start it — the package managers found on the computer.
/// </summary>
public sealed class FfmpegService(
    SettingsService settings, IHttpClientFactory httpFactory, EventBus events, ILogger<FfmpegService> logger)
{
    private static readonly string[] KnownEncoders = ["libx265", "libx264", "libsvtav1"];
    private static readonly string[] ToolNames = ["ffmpeg", "ffprobe"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private FfmpegTools? _tools;
    private string _toolsKey = "";
    private bool _looked;
    private string? _problem; // an ffmpeg was found but does not run
    private bool _blocked;    // … because the system refuses to start it
    private Task? _install;
    private CancellationTokenSource? _installCts;
    private string _installState = ""; // "", downloading, installing, error
    private string? _installMessage;
    private long _downloaded, _total;
    private IReadOnlyList<string> _tried = [];

    /// <summary>Where the app keeps the ffmpeg it downloaded.</summary>
    public string AppDirectory { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Subtitall", "ffmpeg");

    private static string Os =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";

    /// <summary>The PATH, then the folders package managers install ffmpeg into.</summary>
    private static IReadOnlyList<string> SearchDirectories() =>
        FfmpegLocator.PathDirectories()
            .Concat(FfmpegPackageManagers.KnownFolders(Os,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

    private bool HasDownload => ToolNames.Any(t => File.Exists(System.IO.Path.Combine(AppDirectory, FfmpegLocator.ExeName(t))));

    /// <summary>The ffmpeg to use, or null when none is installed (or none of those found runs).</summary>
    public async Task<FfmpegTools?> GetAsync(CancellationToken ct)
    {
        var custom = settings.Current.Reencode.FfmpegPath;
        await _gate.WaitAsync(ct);
        try
        {
            // Looked for once, then remembered (found or not) until Forget(): starting a blocked
            // program again on every status request would only anger the antivirus.
            if (_looked && _toolsKey == custom && (_tools is null || (File.Exists(_tools.Ffmpeg) && File.Exists(_tools.Ffprobe))))
                return _tools;
            _looked = true;

            _tools = null;
            _problem = null;
            _blocked = false;
            _toolsKey = custom;
            // The first ffmpeg that actually runs wins: a copy the system refuses to start
            // (security policy, wrong architecture…) must not hide a working one further down.
            foreach (var location in FfmpegLocator.FindAll(custom, AppDirectory, SearchDirectories()))
            {
                try
                {
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeout.CancelAfter(TimeSpan.FromSeconds(20));
                    var version = await FfmpegRunner.RunAsync(location.Ffmpeg, ["-hide_banner", "-version"], timeout.Token);
                    var probe = await FfmpegRunner.RunAsync(location.Ffprobe, ["-hide_banner", "-version"], timeout.Token);
                    if (version.ExitCode != 0 || probe.ExitCode != 0)
                        throw new InvalidOperationException(version.LastError.Length > 0 ? version.LastError : probe.LastError);
                    var encoders = await FfmpegRunner.RunAsync(location.Ffmpeg, ["-hide_banner", "-encoders"], timeout.Token);
                    _tools = new FfmpegTools(location.Ffmpeg, location.Ffprobe, location.Source,
                        FfmpegRunner.ParseVersion(version.StdOut),
                        KnownEncoders.Where(e => encoders.StdOut.Contains($" {e} ", StringComparison.Ordinal)).ToList());
                    _problem = null;
                    _blocked = false;
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "ffmpeg found at {Path} does not run", location.Ffmpeg);
                    _blocked |= IsAccessDenied(ex);
                    _problem ??= WhyItDoesNotRun(location.Ffmpeg, ex);
                }
            }
            return _tools;
        }
        finally
        {
            _gate.Release();
        }
    }

    // What a company computer answers when a policy or the antivirus forbids new programs.
    private static bool IsAccessDenied(Exception ex) =>
        ex is System.ComponentModel.Win32Exception { NativeErrorCode: 5 or 13 } or UnauthorizedAccessException;

    private static string WhyItDoesNotRun(string path, Exception ex) => ex switch
    {
        _ when IsAccessDenied(ex) =>
            $"Your system refuses to start {path} (access denied). A security policy or the antivirus is blocking it — " +
            "common on company computers.",
        OperationCanceledException => $"ffmpeg was found at {path} but it does not answer.",
        _ => $"ffmpeg was found at {path} but it does not run: {ex.Message}",
    };

    /// <summary>Looks for ffmpeg afresh on the next call (the user installed one, or changed the folder).</summary>
    public void Forget()
    {
        _gate.Wait();
        try { _tools = null; _looked = false; }
        finally { _gate.Release(); }
    }

    /// <summary>Deletes the ffmpeg the app downloaded (one the system refuses to start is dead weight).</summary>
    public void RemoveDownload()
    {
        foreach (var tool in ToolNames)
        {
            try { File.Delete(System.IO.Path.Combine(AppDirectory, FfmpegLocator.ExeName(tool))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogWarning(ex, "Could not delete the downloaded {Tool}", tool);
            }
        }
        lock (_lock)
        {
            if (_installState == "error")
                (_installState, _installMessage, _tried) = ("", null, []);
        }
        Forget();
    }

    public async Task<FfmpegStatus> GetStatusAsync(CancellationToken ct)
    {
        string state;
        string? message;
        long downloaded, total;
        IReadOnlyList<string> tried;
        lock (_lock)
        {
            (state, message, downloaded, total, tried) = (_installState, _installMessage, _downloaded, _total, _tried);
        }
        var canDownload = FfmpegDownload.Platform() is not null;
        var fallbacks = FfmpegPackageManagers.Available(Os, SearchDirectories())
            .Select(a => a.Installer.Label + (a.Installer.Elevated ? " (your system asks you to confirm)" : ""))
            .ToList();
        FfmpegStatus Make(string s, FfmpegTools? tools, double? percent, string? text, bool blocked = false) => new(
            s, Os, canDownload, tools?.Source, tools?.Version, tools?.Ffmpeg, tools?.Encoders ?? [], percent,
            downloaded, total, text, AppDirectory, blocked, HasDownload, fallbacks, s == "error" ? tried : []);

        if (state is "downloading" or "installing")
            return Make(state, null, total > 0 ? Math.Round(downloaded * 100.0 / total, 1) : null, message);

        var found = await GetAsync(ct);
        if (found is not null)
            return Make("ready", found, null, null);
        if (state == "error")
            return Make("error", null, null, message, _blocked);
        return Make(_problem is null ? "missing" : "error", null, null, _problem, _blocked);
    }

    /// <summary>Starts installing ffmpeg. False when an install is already running.</summary>
    public bool StartInstall()
    {
        lock (_lock)
        {
            if (_install is { IsCompleted: false })
                return false;
            _installCts = new CancellationTokenSource();
            var token = _installCts.Token;
            (_installState, _installMessage, _downloaded, _total, _tried) = ("downloading", "Looking for the latest ffmpeg…", 0, 0, []);
            _install = Task.Run(() => InstallAsync(token), CancellationToken.None);
        }
        _ = PublishAsync();
        return true;
    }

    public void CancelInstall()
    {
        lock (_lock) _installCts?.Cancel();
    }

    private async Task PublishAsync()
    {
        try { events.Publish("ffmpeg-status", await GetStatusAsync(CancellationToken.None)); }
        catch (Exception ex) { logger.LogDebug(ex, "Could not publish the ffmpeg status"); }
    }

    private void Set(string state, string? message)
    {
        lock (_lock) (_installState, _installMessage) = (state, message);
        _ = PublishAsync();
    }

    /// <summary>
    /// The whole install: the app's own download, then each package manager present, until an
    /// ffmpeg runs. Every way that failed is kept, with its reason, for the user to read.
    /// </summary>
    private async Task InstallAsync(CancellationToken ct)
    {
        var tried = new List<string>();
        try
        {
            // 1. The app's own download.
            string? downloadFailure = null;
            try
            {
                await DownloadIntoAppFolderAsync(ct);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                logger.LogWarning(ex, "ffmpeg download failed");
                downloadFailure = ex.Message;
            }
            Forget();
            var tools = await GetAsync(ct);
            var downloadBlocked = _blocked;
            if (tools?.Source != FfmpegSource.App)
                tried.Add("Direct download: " + (downloadFailure ?? (downloadBlocked ? "downloaded, but your system refuses to start it" : _problem ?? "it did not work")));

            // 2. The package managers of this computer, when nothing runs yet.
            if (tools is null)
            {
                foreach (var (installer, commandPath) in FfmpegPackageManagers.Available(Os, SearchDirectories()))
                {
                    ct.ThrowIfCancellationRequested();
                    Set("installing", (downloadBlocked ? "Your system refused to start the download" : "The download did not work")
                        + $" — installing ffmpeg with {installer.Label} instead…"
                        + (installer.Elevated || Os == "linux" ? " Your system asks you to confirm: accept it." : ""));
                    var failure = await RunInstallerAsync(installer, commandPath, ct);
                    Forget();
                    tools = await GetAsync(ct);
                    if (tools is not null)
                        break;
                    tried.Add($"{installer.Label}: " + (failure
                        ?? (_blocked && !downloadBlocked ? "installed, but your system refuses to start it too" : "ffmpeg was still not found afterwards")));
                }
            }

            // A download that does not run is dead weight — except one the system blocks, which
            // the user may still get allowed (then "Check again" finds it).
            if (tools?.Source != FfmpegSource.App && HasDownload && !(tools is null && downloadBlocked))
                RemoveDownloadedFiles();

            if (tools is null)
            {
                lock (_lock) _tried = tried;
                Set("error", _blocked
                    ? "Your system refuses to start ffmpeg (access denied). A security policy or the antivirus is blocking it — common on company computers."
                    : "ffmpeg could not be installed automatically.");
                return;
            }
            logger.LogInformation("ffmpeg ready at {Path} ({Source})", tools.Ffmpeg, tools.Source);
            Set("", null);
        }
        catch (OperationCanceledException)
        {
            Forget();
            Set("", null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ffmpeg install failed");
            lock (_lock) _tried = tried;
            Set("error", ex.Message);
        }
    }

    private void RemoveDownloadedFiles()
    {
        foreach (var tool in ToolNames)
        {
            try { File.Delete(System.IO.Path.Combine(AppDirectory, FfmpegLocator.ExeName(tool))); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                logger.LogDebug(ex, "Could not delete the downloaded {Tool}", tool);
            }
        }
        Forget();
    }

    /// <summary>Runs a package manager's "install ffmpeg". Returns why it failed, or null when it ended well.</summary>
    private async Task<string?> RunInstallerAsync(PackageInstaller installer, string commandPath, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(TimeSpan.FromMinutes(20));
        try
        {
            var arguments = installer.Arguments;
            int exitCode;
            string lastLine;
            if (installer.Elevated)
            {
                // Administrator rights: the system shows its own confirmation, and the program
                // runs in a window of its own (its output cannot be read from here).
                using var process = Process.Start(new ProcessStartInfo(commandPath, string.Join(' ', arguments))
                {
                    UseShellExecute = true,
                    Verb = "runas",
                    WindowStyle = ProcessWindowStyle.Minimized,
                }) ?? throw new InvalidOperationException("it could not be started");
                try
                {
                    await process.WaitForExitAsync(timeout.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(entireProcessTree: true); } catch (Exception) { /* not ours to stop, or already gone */ }
                    throw;
                }
                (exitCode, lastLine) = (process.ExitCode, "");
            }
            else
            {
                // Scripts (scoop.cmd…) are started through the command interpreter.
                var script = OperatingSystem.IsWindows() && System.IO.Path.GetExtension(commandPath).ToLowerInvariant() is ".cmd" or ".bat";
                var executable = script ? Environment.GetEnvironmentVariable("ComSpec") ?? "cmd.exe" : commandPath;
                IEnumerable<string> all = script ? new[] { "/d", "/c", commandPath }.Concat(arguments) : arguments;
                // On Linux the first argument of pkexec is the package manager itself.
                var output = await FfmpegRunner.RunAsync(executable, all, timeout.Token);
                exitCode = output.ExitCode;
                lastLine = (output.StdErr + "\n" + output.StdOut)
                    .Split('\n', '\r').Select(l => l.Trim()).LastOrDefault(l => l.Any(char.IsLetter)) ?? "";
            }
            logger.LogInformation("{Installer} ended with code {Code}: {Line}", installer.Name, exitCode, lastLine);
            // "Already installed" also ends with an error code: whether ffmpeg runs is what counts,
            // and the caller checks that. This text only explains a failure.
            return exitCode == 0 ? null : $"it ended with an error ({(lastLine.Length > 0 ? Shorten(lastLine) : $"code {exitCode}")})";
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return "it did not finish in time";
        }
        catch (System.ComponentModel.Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            return "you declined the confirmation";
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogWarning(ex, "{Installer} could not be run", installer.Name);
            return IsAccessDenied(ex) ? "your system refuses to start it" : $"it could not be run ({ex.Message})";
        }
    }

    private static string Shorten(string text) => text.Length <= 160 ? text : text[..160] + "…";

    private async Task DownloadIntoAppFolderAsync(CancellationToken ct)
    {
        var work = System.IO.Path.Combine(AppDirectory, "download");
        try
        {
            var platform = FfmpegDownload.Platform()
                ?? throw new InvalidOperationException("no ready-made ffmpeg exists for this kind of computer");
            if (Directory.Exists(work))
                Directory.Delete(work, recursive: true);
            Directory.CreateDirectory(work);

            var http = httpFactory.CreateClient();
            http.DefaultRequestHeaders.UserAgent.ParseAdd($"Subtitall/{UserAgent.Version}");
            var assets = platform == "mac" ? FfmpegDownload.MacAssets() : [await ResolveAssetAsync(http, platform, ct)];

            var archives = new List<string>();
            long doneBefore = 0;
            foreach (var asset in assets)
            {
                var archive = System.IO.Path.Combine(work, asset.Name);
                await DownloadAsync(http, asset, archive, doneBefore, ct);
                doneBefore += new FileInfo(archive).Length;
                archives.Add(archive);
            }

            Set("installing", "Unpacking ffmpeg…");
            var staging = System.IO.Path.Combine(work, "unpacked");
            Directory.CreateDirectory(staging);
            foreach (var archive in archives)
                await UnpackAsync(archive, staging, ct);

            foreach (var tool in ToolNames)
            {
                var name = FfmpegLocator.ExeName(tool);
                var found = Directory.EnumerateFiles(staging, name, SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new InvalidOperationException($"the download does not contain {name}");
                var target = System.IO.Path.Combine(AppDirectory, name);
                File.Move(found, target, overwrite: true);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }
        }
        finally
        {
            try { if (Directory.Exists(work)) Directory.Delete(work, recursive: true); }
            catch (Exception ex) { logger.LogDebug(ex, "Could not clean {Directory}", work); }
        }
    }

    private async Task<FfmpegAsset> ResolveAssetAsync(HttpClient http, string platform, CancellationToken ct)
    {
        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(20));
            var json = await http.GetStringAsync(FfmpegDownload.ReleaseApi, timeout.Token);
            if (FfmpegDownload.PickAsset(FfmpegDownload.ParseRelease(json), platform) is { } asset)
                return asset;
        }
        catch (Exception ex) when (!ct.IsCancellationRequested)
        {
            // GitHub's API has an hourly limit per address; the plain download link has none.
            logger.LogWarning(ex, "Could not list the ffmpeg builds — using the default link");
        }
        return FfmpegDownload.Fallback(platform);
    }

    private async Task DownloadAsync(HttpClient http, FfmpegAsset asset, string target, long doneBefore, CancellationToken ct)
    {
        using var response = await http.GetAsync(asset.Url, HttpCompletionOption.ResponseHeadersRead, ct);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength ?? asset.Size;
        lock (_lock) (_installMessage, _total) = ($"Downloading {asset.Name}…", _total + Math.Max(length, 0));
        _ = PublishAsync();

        using var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        await using (var source = await response.Content.ReadAsStreamAsync(ct))
        await using (var file = File.Create(target))
        {
            var buffer = new byte[1 << 17];
            long done = 0;
            var lastReport = DateTime.UtcNow;
            int read;
            while ((read = await source.ReadAsync(buffer, ct)) > 0)
            {
                await file.WriteAsync(buffer.AsMemory(0, read), ct);
                sha.AppendData(buffer, 0, read);
                done += read;
                if ((DateTime.UtcNow - lastReport).TotalMilliseconds < 300)
                    continue;
                lastReport = DateTime.UtcNow;
                lock (_lock) _downloaded = doneBefore + done;
                _ = PublishAsync();
            }
            lock (_lock) _downloaded = doneBefore + done;
        }

        if (asset.Sha256 is { } expected
            && !string.Equals(Convert.ToHexString(sha.GetHashAndReset()), expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("the download is damaged (checksum mismatch)");
    }

    private static async Task UnpackAsync(string archive, string destination, CancellationToken ct)
    {
        if (archive.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // Only the two programs are needed out of the archive.
            using var zip = ZipFile.OpenRead(archive);
            foreach (var entry in zip.Entries)
            {
                ct.ThrowIfCancellationRequested();
                if (entry.Name is "ffmpeg" or "ffprobe" or "ffmpeg.exe" or "ffprobe.exe")
                    entry.ExtractToFile(System.IO.Path.Combine(destination, entry.Name), overwrite: true);
            }
            return;
        }

        // .tar.xz (Linux): .NET cannot read xz, the system's tar can.
        ProcessOutput result;
        try
        {
            result = await FfmpegRunner.RunAsync("tar", ["-xJf", archive, "-C", destination], ct);
        }
        catch (System.ComponentModel.Win32Exception)
        {
            throw new InvalidOperationException("the 'tar' command is needed to unpack ffmpeg and was not found");
        }
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"unpacking failed: {result.LastError}");
    }
}
