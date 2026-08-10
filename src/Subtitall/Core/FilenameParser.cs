using System.Text.RegularExpressions;

namespace Subtitall.Core;

/// <summary>
/// Extracts show/movie title, season, episode, year and release group from a video filename.
/// Handles common scene naming (S01E02, 1x02), anime absolute numbering ("[Group] Show - 12")
/// and movie naming ("Title.2010.1080p.BluRay").
/// </summary>
public static partial class FilenameParser
{
    // e2 alternation covers "S01E01E02", "S01E01-E02" and "S01E01-02"; the trailing
    // lookahead keeps "-1080p"-style tokens from reading as a second episode.
    [GeneratedRegex(@"(?<![A-Za-z])S(?<s>\d{1,2})[ ._\-]?E(?<e>\d{1,3})(?:(?:[ ._\-]*E|-)(?<e2>\d{1,3})(?![0-9pP]))*", RegexOptions.IgnoreCase)]
    private static partial Regex SeasonEpisodeRegex();

    [GeneratedRegex(@"(?<![A-Za-z0-9])(?<s>\d{1,2})x(?<e>\d{1,3})\b", RegexOptions.IgnoreCase)]
    private static partial Regex AltSeasonEpisodeRegex();

    [GeneratedRegex(@"(?<![A-Za-z])Season[ ._\-]*(?<s>\d{1,2})[ ._\-]*(?:Episode|Ep)[ ._\-]*(?<e>\d{1,3})", RegexOptions.IgnoreCase)]
    private static partial Regex VerboseSeasonEpisodeRegex();

    // "Show - 12", "Show - 12.5" (skip .5 specials), "Show - 12v2" — anime absolute numbering.
    [GeneratedRegex(@"[ ._]-[ ._](?<e>\d{1,4})(?:v\d)?\s*(?:$|[\[\(])")]
    private static partial Regex AbsoluteEpisodeRegex();

    // x/p exclusions keep resolutions ("1920x1080", "2019p") from reading as years.
    [GeneratedRegex(@"(?<![\dxXpP])(?<y>(19|20)\d{2})(?![\dxXpP])")]
    private static partial Regex YearRegex();

    [GeneratedRegex(@"^\[(?<g>[^\]]+)\]")]
    private static partial Regex LeadingGroupRegex();

    [GeneratedRegex(@"-(?<g>[A-Za-z0-9]+)$")]
    private static partial Regex TrailingGroupRegex();

    private static readonly string[] NoiseTokens =
    [
        "480p", "576p", "720p", "1080p", "1440p", "2160p", "4k", "8k",
        "bluray", "blu-ray", "bdrip", "brrip", "bd", "remux",
        "webdl", "web-dl", "webrip", "web", "hdtv", "dvdrip", "dvd", "hdrip", "cam", "ts",
        "x264", "x265", "h264", "h265", "h.264", "h.265", "hevc", "avc", "xvid", "divx", "av1",
        "aac", "ac3", "eac3", "dts", "ddp", "dd5", "dd+", "truehd", "atmos", "flac", "opus", "mp3",
        // separator-split halves of "Blu-Ray", "WEB-DL", "H.264" (separators become spaces before matching)
        "blu", "dl", "264", "265",
        "10bit", "8bit", "hdr", "hdr10", "dv", "dovi", "sdr",
        "proper", "repack", "extended", "unrated", "remastered", "internal", "limited", "complete",
        "multi", "vostfr", "vf", "vo", "subbed", "dubbed", "dual",
    ];

    private static readonly HashSet<string> VideoExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mkv", ".mp4", ".avi", ".mov", ".wmv", ".flv", ".m4v", ".mpg", ".mpeg",
        ".m2ts", ".webm", ".ogm", ".divx", ".3gp", ".vob", ".rmvb",
    };

    public static bool IsVideoFile(string path) => VideoExtensions.Contains(Path.GetExtension(path));

    public static ParsedMedia Parse(string fileName)
    {
        var name = Path.GetFileName(fileName);
        if (VideoExtensions.Contains(Path.GetExtension(name)))
            name = Path.GetFileNameWithoutExtension(name);

        string? releaseGroup = null;

        // Leading "[Group]" (anime convention).
        var leadingGroup = LeadingGroupRegex().Match(name);
        if (leadingGroup.Success)
        {
            releaseGroup = leadingGroup.Groups["g"].Value.Trim();
            name = name[leadingGroup.Length..].TrimStart();
        }

        int? season = null, episode = null, endEpisode = null, absoluteEpisode = null;
        var titleEnd = name.Length;

        var m = SeasonEpisodeRegex().Match(name);
        if (m.Success)
        {
            season = int.Parse(m.Groups["s"].Value);
            episode = int.Parse(m.Groups["e"].Value);
            var extra = m.Groups["e2"];
            if (extra.Success)
                endEpisode = int.Parse(extra.Captures[^1].Value);
            titleEnd = m.Index;
        }
        else if ((m = AltSeasonEpisodeRegex().Match(name)).Success)
        {
            season = int.Parse(m.Groups["s"].Value);
            episode = int.Parse(m.Groups["e"].Value);
            titleEnd = m.Index;
        }
        else if ((m = VerboseSeasonEpisodeRegex().Match(name)).Success)
        {
            season = int.Parse(m.Groups["s"].Value);
            episode = int.Parse(m.Groups["e"].Value);
            titleEnd = m.Index;
        }
        else if ((m = AbsoluteEpisodeRegex().Match(name)).Success && !IsYearLike(m.Groups["e"].Value))
        {
            absoluteEpisode = int.Parse(m.Groups["e"].Value);
            titleEnd = m.Index;
        }

        // Year: last standalone year token wins ("1917.2019" → year 2019, title "1917").
        int? year = null;
        var yearMatches = YearRegex().Matches(name);
        if (yearMatches.Count > 0)
        {
            var ym = yearMatches[^1];
            year = int.Parse(ym.Groups["y"].Value);
            // The year delimits the title ("Inception.2010.1080p", "Doctor.Who.2005.S04E12")
            // unless the name starts with it — then it IS the title ("2012.2009.1080p").
            if (ym.Index > 0 && ym.Index < titleEnd)
                titleEnd = ym.Index;
        }

        var title = CleanTitle(name[..titleEnd]);

        // If nothing matched, the "title" may still carry noise tokens — cut at the first one.
        title = CutAtNoise(title);

        // Trailing "-GROUP" on scene releases. Only trust it when the name carries scene
        // markers — otherwise hyphenated titles lose their tail ("Spider-Man" → group "Man").
        var looksLikeSceneRelease = season.HasValue || absoluteEpisode.HasValue || year.HasValue || ContainsNoise(name);
        if (releaseGroup is null && looksLikeSceneRelease)
        {
            var trailing = TrailingGroupRegex().Match(name.Trim());
            if (trailing.Success && !IsNoise(trailing.Groups["g"].Value) && !IsYearLike(trailing.Groups["g"].Value))
                releaseGroup = trailing.Groups["g"].Value;
        }

        return new ParsedMedia
        {
            Title = title,
            Season = season,
            Episode = episode,
            EndEpisode = endEpisode,
            AbsoluteEpisode = absoluteEpisode,
            Year = year,
            ReleaseGroup = releaseGroup,
        };
    }

    private static bool IsYearLike(string value) =>
        value.Length == 4 && (value.StartsWith("19") || value.StartsWith("20"));

    private static bool ContainsNoise(string name) =>
        Regex.Split(name.ToLowerInvariant(), @"[ ._\-\[\]\(\)]+").Any(IsNoise);

    private static bool IsNoise(string token)
    {
        token = token.Trim().ToLowerInvariant().TrimEnd('.');
        return NoiseTokens.Contains(token) || Regex.IsMatch(token, @"^(dd[p+]?[257]([ .]?[01])?|\d{3,4}p)$");
    }

    private static string CutAtNoise(string title)
    {
        var words = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var kept = new List<string>();
        foreach (var word in words)
        {
            if (IsNoise(word))
                break;
            kept.Add(word);
        }
        return kept.Count > 0 ? string.Join(' ', kept) : title;
    }

    private static string CleanTitle(string raw)
    {
        // Dots and underscores are word separators in scene names.
        var s = raw.Replace('.', ' ').Replace('_', ' ');
        // Keep bracket contents ("The Office (US)") but drop the bracket characters.
        s = Regex.Replace(s, @"[\[\]\(\)\{\}]", " ");
        s = s.Replace('-', ' ');
        s = Regex.Replace(s, @"\s+", " ").Trim();
        return s;
    }
}
