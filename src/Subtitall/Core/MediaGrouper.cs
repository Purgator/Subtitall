using System.Text.RegularExpressions;

namespace Subtitall.Core;

public enum MediaGroupKind
{
    /// <summary>Episodes of one detected show, whatever name each file uses for it.</summary>
    Show,

    /// <summary>Episodes sharing a filename title whose show is not known (yet).</summary>
    Title,

    /// <summary>Movies of one series ("Harry Potter and the …").</summary>
    MovieSeries,

    /// <summary>Everything else: lone movies and files nothing is known about.</summary>
    Other,
}

/// <summary>
/// A set of files of one show that belong together season-wise: same folder, same filename
/// title, same season written in the name (or none). The user sets the season per block.
/// </summary>
public sealed record SeasonBlock(string Key, IReadOnlyList<MediaItem> Items);

/// <summary>Files that are handled together: one show, or one movie series.</summary>
public sealed record MediaGroup(
    string Key, MediaGroupKind Kind, string Name, IReadOnlyList<MediaItem> Items, IReadOnlyList<SeasonBlock> Blocks);

/// <summary>
/// Splits the library into the blocks the UI shows: one per show (files named after different
/// titles of the same show end up together), one per movie series, and one for the rest.
/// Groups come in the order their first file was added, the rest last.
/// </summary>
public static class MediaGrouper
{
    public const string OtherKey = "other";

    // Words that say nothing about which series a movie belongs to.
    private static readonly HashSet<string> Fillers = new(StringComparer.OrdinalIgnoreCase)
    {
        "the", "a", "an", "and", "of", "in", "on", "at", "to", "for", "with", "from", "vs",
        "part", "chapter", "episode", "vol", "volume", "le", "la", "les", "de", "du", "des", "et",
    };

    public static IReadOnlyList<MediaGroup> Group(IReadOnlyList<MediaItem> items)
    {
        var seriesOf = MovieSeries(items.Where(i => !i.Parsed.IsEpisode && !i.IsUnidentified).ToList());

        var order = new List<string>();
        var members = new Dictionary<string, List<MediaItem>>();
        var meta = new Dictionary<string, (MediaGroupKind Kind, string Name)>();
        foreach (var item in items)
        {
            string key, name;
            MediaGroupKind kind;
            if (item.Parsed.IsEpisode && item.Detection?.Show is { } show)
                (key, kind, name) = ($"show:{ShowDetectionService.Key(show.Show)}", MediaGroupKind.Show, show.Show.Name);
            else if (item.Parsed.IsEpisode)
                (key, kind, name) = ($"title:{item.Parsed.Title.Trim().ToLowerInvariant()}", MediaGroupKind.Title, item.Parsed.Title.Trim());
            else if (seriesOf.TryGetValue(item.Id, out var series))
                (key, kind, name) = ($"movies:{series.ToLowerInvariant()}", MediaGroupKind.MovieSeries, series);
            else
                (key, kind, name) = (OtherKey, MediaGroupKind.Other, "Other files");

            if (!members.TryGetValue(key, out var list))
            {
                members[key] = list = [];
                meta[key] = (kind, name);
                order.Add(key);
            }
            list.Add(item);
        }

        return order
            .OrderBy(key => key == OtherKey) // Stable: keeps first-seen order, the rest last.
            .Select(key => new MediaGroup(key, meta[key].Kind, meta[key].Name, members[key],
                meta[key].Kind is MediaGroupKind.Show or MediaGroupKind.Title ? Blocks(members[key]) : []))
            .ToList();
    }

    /// <summary>Season blocks of one show's files, ordered by the season they are seen as.</summary>
    private static IReadOnlyList<SeasonBlock> Blocks(List<MediaItem> items) =>
        items
            .GroupBy(BlockKey)
            .Select(g => new SeasonBlock(g.Key, g.ToList()))
            .OrderBy(b => b.Items.Min(i => i.Effective.Season is int s ? (s == 0 ? int.MaxValue - 1 : s) : int.MaxValue))
            .ToList();

    private static string BlockKey(MediaItem item)
    {
        var p = item.Parsed;
        var season = p.Season is int s ? $"s{s}" : p.SeasonHint is int h ? $"h{h}" : p.FinalSeasonHint ? "final" : "bare";
        var folder = Path.GetDirectoryName(item.Path) ?? "";
        return $"{folder}|{p.Title.Trim()}|{season}".ToLowerInvariant();
    }

    /// <summary>
    /// Item id → series name, for movies whose titles start with the same words: at least two
    /// meaningful ones ("Harry Potter and the …"), or one movie's whole title ("Alien", "Alien 3").
    /// </summary>
    private static Dictionary<string, string> MovieSeries(List<MediaItem> movies)
    {
        var words = movies.Select(m => Words(m.Parsed.Title)).ToList();
        var parent = Enumerable.Range(0, movies.Count).ToArray();
        int Find(int i) => parent[i] == i ? i : parent[i] = Find(parent[i]);

        for (var a = 0; a < movies.Count; a++)
        {
            for (var b = a + 1; b < movies.Count; b++)
            {
                var common = CommonPrefix(words[a], words[b]);
                if (IsSeriesPrefix(words[a], words[b], common))
                    parent[Find(b)] = Find(a);
            }
        }

        var result = new Dictionary<string, string>();
        foreach (var cluster in Enumerable.Range(0, movies.Count).GroupBy(Find).Where(g => g.Count() > 1))
        {
            var indexes = cluster.ToList();
            var first = words[indexes[0]];
            var shared = indexes.Skip(1).Aggregate(first.Count, (n, i) => Math.Min(n, CommonPrefix(first, words[i])));
            var name = SeriesName(first, shared);
            foreach (var i in indexes)
                result[movies[i].Id] = name;
        }
        return result;
    }

    private static bool IsSeriesPrefix(List<string> a, List<string> b, int common)
    {
        var meaningful = a.Take(common).Count(IsMeaningful);
        if (meaningful >= 2)
            return true;
        // "Alien" + "Alien 3": the shorter title is the whole shared part.
        return meaningful >= 1 && common == Math.Min(a.Count(w => !IsNumber(w)), b.Count(w => !IsNumber(w)))
            && (a.Skip(common).All(IsNumber) || b.Skip(common).All(IsNumber));
    }

    private static string SeriesName(List<string> words, int shared)
    {
        var kept = words.Take(shared).ToList();
        while (kept.Count > 0 && !IsMeaningful(kept[^1]))
            kept.RemoveAt(kept.Count - 1);
        return string.Join(' ', kept.Count > 0 ? kept : words);
    }

    private static int CommonPrefix(List<string> a, List<string> b)
    {
        var n = 0;
        while (n < a.Count && n < b.Count && string.Equals(a[n], b[n], StringComparison.OrdinalIgnoreCase))
            n++;
        return n;
    }

    private static List<string> Words(string title)
    {
        var words = Regex.Split(title.Replace("'", "").Replace("’", ""), @"[^\p{L}\p{N}]+").Where(w => w.Length > 0).ToList();
        // A leading article is not part of the series name ("The Matrix", "Matrix Reloaded").
        if (words.Count > 1 && words[0].ToLowerInvariant() is "the" or "a" or "an")
            words.RemoveAt(0);
        return words;
    }

    private static bool IsNumber(string word) =>
        word.All(char.IsDigit) || Regex.IsMatch(word, "^(?=[IVX])(X{0,2})(IX|IV|V?I{0,3})$", RegexOptions.IgnoreCase);

    private static bool IsMeaningful(string word) => !Fillers.Contains(word) && !IsNumber(word);
}
