using System.Globalization;
using System.Text.RegularExpressions;

namespace Subtitall.Core.Sync;

/// <summary>
/// Moves and stretches the times of a subtitle file in place, leaving everything else (text,
/// styles, line endings) as it is: <c>new = scale × old + offset</c>, never before zero.
/// </summary>
public static partial class SubtitleRetimer
{
    public static string Retime(string text, string extension, double scale, double offset) =>
        extension.ToLowerInvariant() switch
        {
            ".srt" => RetimeTimed(text, scale, offset, comma: true),
            ".vtt" => RetimeTimed(text, scale, offset, comma: false),
            ".ass" or ".ssa" => RetimeAss(text, scale, offset),
            _ => throw new NotSupportedException($"Cannot retime {extension} files"),
        };

    private static string RetimeTimed(string text, double scale, double offset, bool comma) =>
        TimingLine().Replace(text, m =>
        {
            if (!SubtitleParser.TryParseTime(m.Groups["a"].Value, out var start)
                || !SubtitleParser.TryParseTime(m.Groups["b"].Value, out var end))
                return m.Value;
            return $"{Format(Move(start, scale, offset), comma)}{m.Groups["mid"].Value}{Format(Move(end, scale, offset), comma)}";
        });

    private static string RetimeAss(string text, double scale, double offset) =>
        AssLine().Replace(text, m =>
        {
            if (!SubtitleParser.TryParseTime(m.Groups["s"].Value, out var start)
                || !SubtitleParser.TryParseTime(m.Groups["e"].Value, out var end))
                return m.Value;
            return $"{m.Groups["pre"].Value}{FormatAss(Move(start, scale, offset))},{FormatAss(Move(end, scale, offset))}{m.Groups["rest"].Value}";
        });

    private static double Move(double seconds, double scale, double offset) => Math.Max(0, seconds * scale + offset);

    private static string Format(double seconds, bool comma)
    {
        var ms = (long)Math.Round(seconds * 1000);
        return string.Create(CultureInfo.InvariantCulture,
            $"{ms / 3_600_000:00}:{ms / 60_000 % 60:00}:{ms / 1000 % 60:00}{(comma ? ',' : '.')}{ms % 1000:000}");
    }

    private static string FormatAss(double seconds)
    {
        var cs = (long)Math.Round(seconds * 100);
        return string.Create(CultureInfo.InvariantCulture,
            $"{cs / 360_000}:{cs / 6000 % 60:00}:{cs / 100 % 60:00}.{cs % 100:00}");
    }

    [GeneratedRegex(@"(?<a>(?:\d{1,2}:)?\d{1,2}:\d{2}[,.]\d{1,3})(?<mid>[ \t]*-->[ \t]*)(?<b>(?:\d{1,2}:)?\d{1,2}:\d{2}[,.]\d{1,3})")]
    private static partial Regex TimingLine();

    [GeneratedRegex(@"^(?<pre>[ \t]*(?:Dialogue|Comment):[ \t]*[^,\r\n]*,)(?<s>\d+:\d{2}:\d{2}\.\d{1,2}),(?<e>\d+:\d{2}:\d{2}\.\d{1,2})(?<rest>,.*)$", RegexOptions.Multiline)]
    private static partial Regex AssLine();
}
