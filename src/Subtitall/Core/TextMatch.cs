using System.Text.RegularExpressions;

namespace Subtitall.Core;

/// <summary>Token-based text similarity used for release matching and show matching.</summary>
public static class TextMatch
{
    public static HashSet<string> Tokenize(string text) =>
        Regex.Split(text.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(t => t.Length > 1)
            .ToHashSet();

    /// <summary>Jaccard similarity (0..1) — strict, used to score release names.</summary>
    public static double Jaccard(string a, string b)
    {
        var setA = Tokenize(a);
        var setB = Tokenize(b);
        if (setA.Count == 0 || setB.Count == 0)
            return 0;
        return (double)setA.Intersect(setB).Count() / setA.Union(setB).Count();
    }

    /// <summary>
    /// Containment (0..1): how much of the smaller token set is covered by the other.
    /// Tolerant to one side being longer ("Frieren" vs "Frieren: Beyond Journey's End" → 1.0).
    /// </summary>
    public static double Containment(string a, string b)
    {
        var setA = Tokenize(a);
        var setB = Tokenize(b);
        if (setA.Count == 0 || setB.Count == 0)
            return 1; // Can't tell — treat as matching rather than blocking.
        return (double)setA.Intersect(setB).Count() / Math.Min(setA.Count, setB.Count);
    }

    /// <summary>"Shingeki no Kyojin" → "snk", "Attack on Titan" → "aot".</summary>
    public static string Acronym(string name) =>
        string.Concat(Regex.Split(name.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(t => t.Length > 0)
            .Select(t => t[0]));

    /// <summary>
    /// True when a filename title plausibly refers to a show known under any of the given
    /// names: token containment against each name, or an acronym match ("SnK" for
    /// "Shingeki no Kyojin", "AoT" for "Attack on Titan").
    /// </summary>
    public static bool MatchesShowName(string parsedTitle, IEnumerable<string> names)
    {
        var trimmed = parsedTitle.Trim();
        var isAcronymCandidate = trimmed.Length is >= 2 and <= 6 && !trimmed.Contains(' ');
        foreach (var name in names)
        {
            if (Containment(parsedTitle, name) >= 0.6)
                return true;
            if (isAcronymCandidate && string.Equals(trimmed, Acronym(name), StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
