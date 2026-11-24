using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Subtitall.Core.Sync;

/// <summary>One subtitle shown from <see cref="Start"/> to <see cref="End"/> (seconds), with its plain text.</summary>
public sealed record SubtitleCue(double Start, double End, string Text)
{
    public double Duration => End - Start;

    /// <summary>Somebody speaks: not a sound description, a song or a typeset sign.</summary>
    public bool IsSpeech => SubtitleParser.IsSpeech(Text);
}

/// <summary>Reads the timing and the text of SRT, WebVTT and ASS/SSA subtitle files.</summary>
public static partial class SubtitleParser
{
    public static bool IsSupported(string extension) =>
        extension.ToLowerInvariant() is ".srt" or ".vtt" or ".ass" or ".ssa";

    /// <summary>UTF-8 (with or without BOM), UTF-16 with BOM, else Latin-1 — what subtitle files come in.</summary>
    public static string Decode(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        try
        {
            return new UTF8Encoding(false, throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(bytes);
        }
    }

    /// <summary>The cues of a file, in time order. Empty when the format is not one of the supported ones.</summary>
    public static IReadOnlyList<SubtitleCue> Parse(string text, string extension)
    {
        var cues = extension.ToLowerInvariant() switch
        {
            ".srt" or ".vtt" => ParseTimed(text),
            ".ass" or ".ssa" => ParseAss(text),
            _ => [],
        };
        return cues.Where(c => c.End > c.Start && c.Start >= 0).OrderBy(c => c.Start).ToList();
    }

    // ---- SRT / WebVTT --------------------------------------------------------------------------

    private static List<SubtitleCue> ParseTimed(string text)
    {
        var cues = new List<SubtitleCue>();
        var lines = text.Split('\n');
        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var arrow = line.IndexOf("-->", StringComparison.Ordinal);
            if (arrow < 0)
                continue;

            var left = line[..arrow].Trim();
            var right = line[(arrow + 3)..].Trim();
            // WebVTT cue settings ("align:start position:10%") follow the end time.
            var space = right.IndexOfAny([' ', '\t']);
            if (space > 0)
                right = right[..space];
            if (!TryParseTime(left, out var start) || !TryParseTime(right, out var end))
                continue;

            var body = new List<string>();
            while (i + 1 < lines.Length && lines[i + 1].Trim().Length > 0 && !lines[i + 1].Contains("-->", StringComparison.Ordinal))
                body.Add(lines[++i].Trim());
            cues.Add(new SubtitleCue(start, end, CleanText(string.Join('\n', body))));
        }
        return cues;
    }

    // ---- ASS / SSA -----------------------------------------------------------------------------

    private static List<SubtitleCue> ParseAss(string text)
    {
        var cues = new List<SubtitleCue>();
        var inEvents = false;
        int fields = 10, startAt = 1, endAt = 2, textAt = 9;
        foreach (var raw in text.Split('\n'))
        {
            var line = raw.Trim();
            if (line.StartsWith('['))
            {
                inEvents = line.Equals("[Events]", StringComparison.OrdinalIgnoreCase);
                continue;
            }
            if (!inEvents)
                continue;

            if (line.StartsWith("Format:", StringComparison.OrdinalIgnoreCase))
            {
                var names = line[7..].Split(',').Select(n => n.Trim()).ToList();
                fields = names.Count;
                startAt = names.FindIndex(n => n.Equals("Start", StringComparison.OrdinalIgnoreCase));
                endAt = names.FindIndex(n => n.Equals("End", StringComparison.OrdinalIgnoreCase));
                textAt = names.FindIndex(n => n.Equals("Text", StringComparison.OrdinalIgnoreCase));
                continue;
            }
            if (!line.StartsWith("Dialogue:", StringComparison.OrdinalIgnoreCase) || startAt < 0 || endAt < 0 || textAt < 0)
                continue;

            // The text is last and may hold commas: split into at most `fields` parts.
            var parts = line[9..].Split(',', fields);
            if (parts.Length < fields
                || !TryParseTime(parts[startAt].Trim(), out var start)
                || !TryParseTime(parts[endAt].Trim(), out var end))
                continue;

            var content = parts[textAt];
            // Vector drawings (\p1…) are typeset signs, not words.
            if (AssDrawing().IsMatch(content))
                continue;
            var plain = CleanText(content.Replace("\\N", "\n").Replace("\\n", "\n").Replace("\\h", " "));
            if (plain.Length > 0)
                cues.Add(new SubtitleCue(start, end, plain));
        }
        return cues;
    }

    // ---- Helpers -------------------------------------------------------------------------------

    /// <summary>"01:02:03,450", "02:03.450", "0:01:02.45" → seconds.</summary>
    internal static bool TryParseTime(string text, out double seconds)
    {
        seconds = 0;
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3)
            return false;
        if (!double.TryParse(parts[^1].Replace(',', '.'), NumberStyles.Float, CultureInfo.InvariantCulture, out var secs)
            || !int.TryParse(parts[^2], NumberStyles.None, CultureInfo.InvariantCulture, out var minutes))
            return false;
        var hours = 0;
        if (parts.Length == 3 && !int.TryParse(parts[0], NumberStyles.None, CultureInfo.InvariantCulture, out hours))
            return false;
        seconds = hours * 3600.0 + minutes * 60.0 + secs;
        return true;
    }

    private static string CleanText(string text)
    {
        text = TagPattern().Replace(text, "");
        text = OverridePattern().Replace(text, "");
        return text.Replace("&nbsp;", " ").Replace("&amp;", "&").Trim();
    }

    /// <summary>
    /// True when the text holds words somebody says. Sound descriptions ("[door slams]",
    /// "(sighs)"), song lines (♪) and bare symbols are not speech the audio would show.
    /// </summary>
    public static bool IsSpeech(string text)
    {
        foreach (var line in text.Split('\n'))
        {
            if (line.Contains('♪') || line.Contains('♫') || line.Contains('♬'))
                continue;
            var words = SoundDescription().Replace(line, " ");
            if (words.Any(char.IsLetterOrDigit))
                return true;
        }
        return false;
    }

    [GeneratedRegex(@"<[^>]*>")]
    private static partial Regex TagPattern();

    [GeneratedRegex(@"\{[^}]*\}")]
    private static partial Regex OverridePattern();

    [GeneratedRegex(@"\[[^\]]*\]|\([^)]*\)")]
    private static partial Regex SoundDescription();

    [GeneratedRegex(@"\{[^}]*\\p[1-9]")]
    private static partial Regex AssDrawing();
}
