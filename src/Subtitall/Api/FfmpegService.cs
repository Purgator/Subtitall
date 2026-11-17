using System.IO.Compression;
using System.Security.Cryptography;
using Subtitall.Core;
using Subtitall.Core.Reencode;

namespace Subtitall.Api;

/// <summary>An ffmpeg that was found and runs.</summary>
public sealed record FfmpegTools(string Ffmpeg, string Ffprobe, FfmpegSource Source, string Version, IReadOnlyList<string> Encoders);

/// <summary>
/// What the UI is told about ffmpeg. States: ready, missing, downloading, installing, error.
/// </summary>
public sealed record FfmpegStatus(
    string State, string Os, bool CanDownload, FfmpegSource? Source, string? Version, string? Path,
    IReadOnlyList<string> Encoders, double? Percent, long DownloadedBytes, long TotalBytes, string? Message, string InstallFolder);

/// <summary>
/// Finds the ffmpeg the Re-encode tab works with, and downloads one into the app's own folder
/// when the user asks for it (Windows and Linux: the BtbN builds; macOS: the evermeet.cx builds).
/// </summary>
public sealed class FfmpegService(
    SettingsService settings, IHttpClientFactory httpFactory, EventBus events, ILogger<FfmpegService> logger)
{
    private static readonly string[] KnownEncoders = ["libx265", "libx264", "libsvtav1"];

    private readonly SemaphoreSlim _gate = new(1, 1);
    private readonly Lock _lock = new();
    private FfmpegTools? _tools;
    private string _toolsKey = "";
    private bool _looked;
    private string? _problem; // an ffmpeg was found but does not run
    private Task? _install;
    private CancellationTokenSource? _installCts;
    private string _installState = ""; // "", downloading, installing, error
    private string? _installMessage;
    private long _downloaded, _total;

    /// <summary>Where the app keeps the ffmpeg it downloaded.</summary>
    public string AppDirectory { get; } =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Subtitall", "ffmpeg");

    private static string Os =>
        OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "mac" : "linux";

    /// <summary>The ffmpeg to use, or null when none is installed (or the one found does not run).</summary>
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
            _toolsKey = custom;
            // The first ffmpeg that actually runs wins: a copy the system refuses to start
            // (security policy, wrong architecture…) must not hide a working one further down.
            foreach (var location in FfmpegLocator.FindAll(custom, AppDirectory, FfmpegLocator.PathDirectories()))
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
                    break;
                }
                catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
                {
                    logger.LogWarning(ex, "ffmpeg found at {Path} does not run", location.Ffmpeg);
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

    private static string WhyItDoesNotRun(string path, Exception ex) => ex switch
    {
        // What a company computer answers when a policy or the antivirus forbids new programs.
        System.ComponentModel.Win32Exception { NativeErrorCode: 5 } or UnauthorizedAccessException =>
            $"Your system refused to start {path} (access denied). A security policy or the antivirus is blocking it — " +
            "common on company computers. Ask your administrator to allow it, or use an ffmpeg installed another way.",
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

    public async Task<FfmpegStatus> GetStatusAsync(CancellationToken ct)
    {
        string state;
        string? message;
        long downloaded, total;
        lock (_lock)
        {
            (state, message, downloaded, total) = (_installState, _installMessage, _downloaded, _total);
        }
        var canDownload = FfmpegDownload.Platform() is not null;
        if (state is "downloading" or "installing")
        {
            return new FfmpegStatus(state, Os, canDownload, null, null, null, [],
                total > 0 ? Math.Round(downloaded * 100.0 / total, 1) : null, downloaded, total, message, AppDirectory);
        }

        var tools = await GetAsync(ct);
        if (tools is not null)
            return new FfmpegStatus("ready", Os, canDownload, tools.Source, tools.Version, tools.Ffmpeg, tools.Encoders, null, 0, 0, null, AppDirectory);
        if (state == "error")
            return new FfmpegStatus("error", Os, canDownload, null, null, null, [], null, 0, 0, message, AppDirectory);
        return new FfmpegStatus(_problem is null ? "missing" : "error", Os, canDownload, null, null, null, [], null, 0, 0, _problem, AppDirectory);
    }

    /// <summary>Starts downloading ffmpeg into the app's folder. False when a download is already running.</summary>
    public bool StartInstall()
    {
        lock (_lock)
        {
            if (_install is { IsCompleted: false })
                return false;
            _installCts = new CancellationTokenSource();
            var token = _installCts.Token;
            (_installState, _installMessage, _downloaded, _total) = ("downloading", "Looking for the latest ffmpeg…", 0, 0);
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

    private async Task InstallAsync(CancellationToken ct)
    {
        var work = System.IO.Path.Combine(AppDirectory, "download");
        try
        {
            var platform = FfmpegDownload.Platform()
                ?? throw new InvalidOperationException("No ready-made ffmpeg exists for this kind of computer — install it yourself.");
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

            foreach (var tool in new[] { "ffmpeg", "ffprobe" })
            {
                var name = FfmpegLocator.ExeName(tool);
                var found = Directory.EnumerateFiles(staging, name, SearchOption.AllDirectories).FirstOrDefault()
                    ?? throw new InvalidOperationException($"The download does not contain {name}.");
                var target = System.IO.Path.Combine(AppDirectory, name);
                File.Move(found, target, overwrite: true);
                if (!OperatingSystem.IsWindows())
                    File.SetUnixFileMode(target, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                        | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            Forget();
            lock (_lock) (_installState, _installMessage) = ("", null);
            var tools = await GetAsync(CancellationToken.None);
            if (tools?.Source != FfmpegSource.App && string.IsNullOrWhiteSpace(settings.Current.Reencode.FfmpegPath))
            {
                // The download does not run here (blocked by a policy, wrong kind of computer):
                // don't leave hundreds of megabytes of dead weight behind.
                var problem = _problem;
                foreach (var tool in new[] { "ffmpeg", "ffprobe" })
                    File.Delete(System.IO.Path.Combine(AppDirectory, FfmpegLocator.ExeName(tool)));
                Forget();
                if (tools is null)
                    throw new InvalidOperationException(problem ?? "ffmpeg was installed but cannot be started.");
            }
            logger.LogInformation("ffmpeg installed in {Directory}", AppDirectory);
            await PublishAsync();
        }
        catch (OperationCanceledException)
        {
            Set("", null);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "ffmpeg install failed");
            Set("error", ex.Message);
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
            throw new InvalidOperationException("The download is damaged (checksum mismatch) — try again.");
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
            throw new InvalidOperationException("The 'tar' command is needed to unpack ffmpeg and was not found.");
        }
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Unpacking failed: {result.LastError}");
    }
}
