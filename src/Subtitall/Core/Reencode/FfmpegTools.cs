using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Subtitall.Core.Reencode;

/// <summary>Where an ffmpeg came from: the user's own folder, the app's download, or the PATH.</summary>
public enum FfmpegSource { Custom, App, Path }

public sealed record FfmpegLocation(string Ffmpeg, string Ffprobe, FfmpegSource Source);

/// <summary>Finds ffmpeg and ffprobe on this computer.</summary>
public static class FfmpegLocator
{
    public static string ExeName(string tool) => OperatingSystem.IsWindows() ? tool + ".exe" : tool;

    /// <summary>
    /// Every place an ffmpeg sits in, best first: the folder the user set, then the app's own
    /// download, then the PATH. Both tools must sit in the same folder.
    /// </summary>
    public static IReadOnlyList<FfmpegLocation> FindAll(string? customPath, string appDirectory, IEnumerable<string> pathDirectories)
    {
        var found = new List<FfmpegLocation>();
        if (!string.IsNullOrWhiteSpace(customPath))
        {
            // A file (…/ffmpeg.exe) or a folder, with or without its bin subfolder.
            var folder = File.Exists(customPath) ? Path.GetDirectoryName(customPath) ?? customPath : customPath;
            foreach (var candidate in new[] { folder, Path.Combine(folder, "bin") })
                if (InFolder(candidate, FfmpegSource.Custom) is { } custom)
                    found.Add(custom);
        }
        if (InFolder(appDirectory, FfmpegSource.App) is { } app)
            found.Add(app);
        foreach (var directory in pathDirectories)
            if (InFolder(directory, FfmpegSource.Path) is { } onPath)
                found.Add(onPath);
        return found;
    }

    /// <summary>The first of <see cref="FindAll"/>, or null when there is no ffmpeg anywhere.</summary>
    public static FfmpegLocation? Find(string? customPath, string appDirectory, IEnumerable<string> pathDirectories) =>
        FindAll(customPath, appDirectory, pathDirectories).FirstOrDefault();

    private static FfmpegLocation? InFolder(string folder, FfmpegSource source)
    {
        try
        {
            var ffmpeg = Path.Combine(folder, ExeName("ffmpeg"));
            var ffprobe = Path.Combine(folder, ExeName("ffprobe"));
            return File.Exists(ffmpeg) && File.Exists(ffprobe) ? new FfmpegLocation(ffmpeg, ffprobe, source) : null;
        }
        catch (ArgumentException)
        {
            return null; // a PATH entry with characters no path can have
        }
    }

    /// <summary>
    /// The folders of the PATH. On Windows the registry is read too: a program installed while
    /// the app runs (winget…) is found without restarting it.
    /// </summary>
    public static IReadOnlyList<string> PathDirectories()
    {
        var values = new List<string?> { Environment.GetEnvironmentVariable("PATH") };
        if (OperatingSystem.IsWindows())
        {
            values.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User));
            values.Add(Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine));
        }
        return values
            .SelectMany(v => (v ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(d => Environment.ExpandEnvironmentVariables(d.Trim('"')))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }
}

/// <summary>A downloadable ffmpeg build.</summary>
public sealed record FfmpegAsset(string Name, string Url, long Size, string? Sha256);

/// <summary>Which ffmpeg build the app downloads for this computer.</summary>
public static partial class FfmpegDownload
{
    /// <summary>The ffmpeg project's page lists these builds as its official Windows and Linux ones.</summary>
    public const string ReleaseApi = "https://api.github.com/repos/BtbN/FFmpeg-Builds/releases/tags/latest";

    private const string ReleaseDownload = "https://github.com/BtbN/FFmpeg-Builds/releases/download/latest/";

    /// <summary>"win64", "winarm64", "linux64", "linuxarm64", "mac", or null when no build exists.</summary>
    public static string? Platform()
    {
        var arch = RuntimeInformation.OSArchitecture;
        if (OperatingSystem.IsWindows())
            return arch switch { Architecture.X64 => "win64", Architecture.Arm64 => "winarm64", _ => null };
        if (OperatingSystem.IsLinux())
            return arch switch { Architecture.X64 => "linux64", Architecture.Arm64 => "linuxarm64", _ => null };
        if (OperatingSystem.IsMacOS())
            return "mac";
        return null;
    }

    /// <summary>The assets of a GitHub release, as its API describes them.</summary>
    public static IReadOnlyList<FfmpegAsset> ParseRelease(string json)
    {
        var assets = new List<FfmpegAsset>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("assets", out var list) || list.ValueKind != JsonValueKind.Array)
            return assets;
        foreach (var asset in list.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (string.IsNullOrEmpty(name) || string.IsNullOrEmpty(url))
                continue;
            var size = asset.TryGetProperty("size", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetInt64() : 0;
            var digest = asset.TryGetProperty("digest", out var d) && d.ValueKind == JsonValueKind.String ? d.GetString() : null;
            assets.Add(new FfmpegAsset(name, url, size,
                digest is not null && digest.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase) ? digest[7..] : null));
        }
        return assets;
    }

    /// <summary>
    /// The newest stable build for a platform (static, GPL — the one with x264 and x265), or the
    /// development build when the release lists no stable one.
    /// </summary>
    public static FfmpegAsset? PickAsset(IEnumerable<FfmpegAsset> assets, string platform)
    {
        FfmpegAsset? best = null, master = null;
        var bestVersion = new Version(0, 0);
        foreach (var asset in assets)
        {
            var match = AssetPattern().Match(asset.Name);
            if (!match.Success || match.Groups["platform"].Value != platform)
                continue;
            if (match.Groups["version"].Value == "master")
                master = asset;
            else if (Version.TryParse(match.Groups["version"].Value[1..], out var version) && version > bestVersion)
                (best, bestVersion) = (asset, version);
        }
        return best ?? master;
    }

    /// <summary>The build to fall back on when the release cannot be listed (no checksum then).</summary>
    public static FfmpegAsset Fallback(string platform)
    {
        var name = $"ffmpeg-master-latest-{platform}-gpl.{(platform.StartsWith("win", StringComparison.Ordinal) ? "zip" : "tar.xz")}";
        return new FfmpegAsset(name, ReleaseDownload + name, 0, null);
    }

    /// <summary>macOS builds come as one zip per tool (Intel builds; Apple Silicon runs them through Rosetta).</summary>
    public static IReadOnlyList<FfmpegAsset> MacAssets() =>
    [
        new("ffmpeg.zip", "https://evermeet.cx/ffmpeg/getrelease/zip", 0, null),
        new("ffprobe.zip", "https://evermeet.cx/ffmpeg/getrelease/ffprobe/zip", 0, null),
    ];

    // ffmpeg-n7.1-latest-win64-gpl-7.1.zip / ffmpeg-master-latest-linux64-gpl.tar.xz (not the -shared ones)
    [GeneratedRegex(@"^ffmpeg-(?<version>master|n\d+(?:\.\d+)+)-latest-(?<platform>[a-z0-9]+)-gpl(?:-\d+(?:\.\d+)+)?\.(?:zip|tar\.xz)$")]
    private static partial Regex AssetPattern();
}
