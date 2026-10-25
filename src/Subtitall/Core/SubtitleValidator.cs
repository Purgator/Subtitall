using System.Text;

namespace Subtitall.Core;

/// <summary>
/// Sanity-checks a downloaded payload before it is saved: providers under stress return
/// HTML error pages, CAPTCHA screens or empty bodies with HTTP 200, and saving one of
/// those as "video.en.srt" is worse than trying the next candidate.
/// </summary>
public static class SubtitleValidator
{
    public static bool LooksLikeSubtitle(byte[] content, string extension)
    {
        if (content.Length < 20)
            return false;

        var head = Decode(content, Math.Min(content.Length, 4096)).TrimStart('﻿', ' ', '\r', '\n', '\t');
        if (head.Length == 0)
            return false;

        // HTML error page or a JSON error body. ASS files start with "[Script Info]" and
        // MicroDVD with "{1}{50}", so only the JSON `{"`/`["` openers are rejected.
        if (head.StartsWith("<!DOCTYPE", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("<html", StringComparison.OrdinalIgnoreCase) ||
            head.StartsWith("{\"") || head.StartsWith("[\""))
            return false;

        return extension.ToLowerInvariant() switch
        {
            ".srt" or ".vtt" => head.Contains("-->"),
            ".ass" or ".ssa" => head.Contains("[Script Info]", StringComparison.OrdinalIgnoreCase) ||
                                head.Contains("Dialogue:", StringComparison.OrdinalIgnoreCase),
            ".sub" => true, // MicroDVD/VobSub come in several shapes — presence checks above must do.
            _ => true,
        };
    }

    private static string Decode(byte[] content, int length)
    {
        try
        {
            return Encoding.UTF8.GetString(content, 0, length);
        }
        catch (ArgumentException)
        {
            return Encoding.Latin1.GetString(content, 0, length);
        }
    }
}
