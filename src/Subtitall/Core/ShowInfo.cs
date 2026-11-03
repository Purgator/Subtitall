using System.Text.RegularExpressions;

namespace Subtitall.Core;

/// <summary>What a metadata source knows about a show beyond its episode list.</summary>
public sealed record ShowInfo(
    string Name,
    string? Summary,
    IReadOnlyList<string> Genres,
    string? Status,
    string? Premiered,
    string? Ended,
    string? Network,
    string? ImageUrl,
    string? OfficialSite,
    double? Rating,
    string? TrailerUrl,
    string Source)
{
    /// <summary>TVMaze summaries are HTML; strip tags for display (paragraphs and line breaks become spaces).</summary>
    public static string? PlainText(string? html)
    {
        if (html is null)
            return null;
        var text = Regex.Replace(html, @"<(?:/p|br\s*/?|/div|/li)>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<[^>]+>", "");
        text = System.Net.WebUtility.HtmlDecode(text);
        return Regex.Replace(text, @"\s+", " ").Trim();
    }
}

/// <summary>One episode of the show, with whether a linked file covers it.</summary>
public sealed record CoveredEpisode(
    int Season, int Number, string Name, string? Airdate, bool Aired, bool Have, string? FileName);

/// <summary>A season's episodes in viewing order, with counts.</summary>
public sealed record SeasonCoverage(int Season, IReadOnlyList<CoveredEpisode> Episodes)
{
    public int Aired => Episodes.Count(e => e.Aired);
    public int Have => Episodes.Count(e => e.Have);
    public int Missing => Episodes.Count(e => e.Aired && !e.Have);
    public int Unaired => Episodes.Count(e => !e.Aired);
}

/// <summary>Which of a show's episodes the linked files cover, season by season.</summary>
public static class EpisodeCoverage
{
    /// <summary>A linked file as the coverage sees it: the episode(s) it holds.</summary>
    public sealed record LinkedFile(int Season, int Episode, int? EndEpisode, string FileName);

    public static IReadOnlyList<SeasonCoverage> Compute(
        IReadOnlyList<TvEpisode> episodes, IEnumerable<LinkedFile> linked, DateOnly today)
    {
        var files = new Dictionary<(int, int), string>();
        foreach (var file in linked)
        {
            var last = file.EndEpisode is int end && end > file.Episode ? end : file.Episode;
            for (var n = file.Episode; n <= last; n++)
                files.TryAdd((file.Season, n), file.FileName);
        }

        return episodes
            .Where(e => e.Season > 0)
            .GroupBy(e => e.Season)
            .OrderBy(g => g.Key)
            .Select(g => new SeasonCoverage(g.Key, g
                .OrderBy(e => e.Number)
                .Select(e => new CoveredEpisode(
                    e.Season, e.Number, e.Name, e.Airdate,
                    Aired: HasAired(e.Airdate, today),
                    Have: files.ContainsKey((e.Season, e.Number)),
                    FileName: files.GetValueOrDefault((e.Season, e.Number))))
                .ToList()))
            .ToList();
    }

    /// <summary>An episode without a known air date counts as aired (the sources list it, so it exists).</summary>
    private static bool HasAired(string? airdate, DateOnly today) =>
        !DateOnly.TryParse(airdate, out var date) || date <= today;
}
