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
}
