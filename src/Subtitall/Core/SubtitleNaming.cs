using System.Text.RegularExpressions;

namespace Subtitall.Core;

/// <summary>Naming conventions for subtitle files stored next to their video.</summary>
public static partial class SubtitleNaming
{
    public static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".sub", ".vtt"];

    // What may follow a video's base name for the file to count as ITS subtitle:
    // ".srt", ".en.srt", ".eng.srt", ".pt-br.srt"… Anything longer ("My Show - 03" +
    // ".5.en.srt", "Show.S01E01" + ".PROPER.en.srt") belongs to a different release.
    [GeneratedRegex(@"^\.([A-Za-z]{2,3}([-_][A-Za-z]{2,4})?\.)?(srt|ass|ssa|sub|vtt)$", RegexOptions.IgnoreCase)]
    private static partial Regex CompanionSuffixRegex();

    /// <summary>Path for a downloaded subtitle: "video.en.srt" next to "video.mkv".</summary>
    public static string GetSubtitlePath(string videoPath, string languageCode, string extension = ".srt")
    {
        var directory = Path.GetDirectoryName(videoPath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        return Path.Combine(directory, $"{baseName}.{languageCode}{extension}");
    }

    /// <summary>True if a subtitle for this language already exists next to the video
    /// (either "video.en.srt" style or a bare "video.srt").</summary>
    public static bool HasSubtitle(string videoPath, string languageCode)
    {
        var directory = Path.GetDirectoryName(videoPath) ?? "";
        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        foreach (var ext in SubtitleExtensions)
        {
            if (File.Exists(Path.Combine(directory, $"{baseName}.{languageCode}{ext}")))
                return true;
            if (File.Exists(Path.Combine(directory, baseName + ext)))
                return true;
        }
        return false;
    }

    /// <summary>All existing subtitle files belonging to this video (used when renaming).</summary>
    public static IEnumerable<string> FindCompanionFiles(string videoPath)
    {
        var directory = Path.GetDirectoryName(videoPath);
        if (directory is null || !Directory.Exists(directory))
            yield break;

        var baseName = Path.GetFileNameWithoutExtension(videoPath);
        foreach (var file in Directory.EnumerateFiles(directory, baseName + ".*"))
        {
            if (file.Equals(videoPath, StringComparison.OrdinalIgnoreCase))
                continue;
            // The wildcard is prefix-based — "My Show - 03.*" also matches files of
            // "My Show - 03.5.mkv". Only accept genuine subtitle suffixes of THIS video.
            var suffix = Path.GetFileName(file)[baseName.Length..];
            if (CompanionSuffixRegex().IsMatch(suffix))
                yield return file;
        }
    }
}
