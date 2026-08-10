namespace Subtitall.Core;

/// <summary>Naming conventions for subtitle files stored next to their video.</summary>
public static class SubtitleNaming
{
    public static readonly string[] SubtitleExtensions = [".srt", ".ass", ".ssa", ".sub", ".vtt"];

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
        }
        return false;
    }

    /// <summary>All existing subtitle files sharing the video's base name (used when renaming).</summary>
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
            var ext = Path.GetExtension(file);
            if (SubtitleExtensions.Contains(ext, StringComparer.OrdinalIgnoreCase))
                yield return file;
        }
    }
}
