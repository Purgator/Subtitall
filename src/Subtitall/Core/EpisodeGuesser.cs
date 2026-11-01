namespace Subtitall.Core;

public enum GuessConfidence
{
    /// <summary>Season and episode were written in the filename.</summary>
    Explicit,

    /// <summary>Season inferred from strong evidence (title hint, siblings, user choice, episode list).</summary>
    High,

    /// <summary>A plausible default — worth a look, and easy to override.</summary>
    Low,
}

/// <summary>Season/episode a file most likely is, and why.</summary>
public sealed record EpisodeGuess(int Season, int Episode, int? EndEpisode, GuessConfidence Confidence, string Reason);

/// <summary>What the guesser knows about one file: the parsed name plus the user's season choice.</summary>
public sealed record GuessInput(ParsedMedia Parsed, int? SeasonOverride = null);

/// <summary>
/// Turns parsed filenames into season/episode pairs, filling in the season when the filename
/// carries only an episode number. It reasons over the whole group of files that belong to
/// one show: files that do spell their season vouch for their siblings, a "Season 3" or
/// "The Final Season" in the title is honoured, the show's episode list says which seasons an
/// episode number even fits, and anime-style absolute numbering is mapped onto seasons when the
/// numbers run past any single season.
/// </summary>
public static class EpisodeGuesser
{
    /// <summary>Guesses for every input, in the same order (null = not an episode / nothing to go on).</summary>
    public static IReadOnlyList<EpisodeGuess?> Guess(
        IReadOnlyList<GuessInput> group, IReadOnlyList<TvEpisode>? episodes)
    {
        var known = episodes is { Count: > 0 }
            ? episodes.Where(e => e.Season > 0).OrderBy(e => e.Season).ThenBy(e => e.Number).ToList()
            : null;
        var episodesPerSeason = known?
            .GroupBy(e => e.Season)
            .ToDictionary(g => g.Key, g => g.Max(e => e.Number));
        var maxSeason = episodesPerSeason is { Count: > 0 } ? episodesPerSeason.Keys.Max() : (int?)null;

        // Evidence from siblings: the seasons spelled out by the other files of the show, and
        // the highest bare number (tells absolute numbering from in-season numbering).
        var siblingSeasons = group
            .Where(i => i.Parsed.Season.HasValue && i.Parsed.Episode.HasValue)
            .Select(i => i.Parsed.Season!.Value)
            .Distinct()
            .ToList();
        var highestBareNumber = group
            .Where(i => !i.Parsed.Season.HasValue && i.Parsed.AbsoluteEpisode.HasValue)
            .Select(i => i.Parsed.AbsoluteEpisode!.Value)
            .DefaultIfEmpty(0)
            .Max();

        return group.Select(input => GuessOne(input, known, episodesPerSeason, maxSeason, siblingSeasons, highestBareNumber)).ToList();
    }

    private static EpisodeGuess? GuessOne(
        GuessInput input,
        List<TvEpisode>? known,
        Dictionary<int, int>? episodesPerSeason,
        int? maxSeason,
        List<int> siblingSeasons,
        int highestBareNumber)
    {
        var p = input.Parsed;
        if (p.Season is int season && p.Episode is int episode)
            return new EpisodeGuess(season, episode, p.EndEpisode, GuessConfidence.Explicit, "Season and episode are in the filename");

        if (p.AbsoluteEpisode is not int number)
            return null;

        if (input.SeasonOverride is int chosen)
            return new EpisodeGuess(chosen, number, null, GuessConfidence.High, "Season chosen by you");

        if (p.SeasonHint is int hinted)
            return new EpisodeGuess(hinted, number, null, GuessConfidence.High, $"The filename says \"Season {hinted}\"");

        if (p.FinalSeasonHint)
        {
            return maxSeason is int last
                ? new EpisodeGuess(last, number, null, GuessConfidence.High, $"\"The Final Season\" is season {last}")
                : null; // Needs the episode list to know which season is the last.
        }

        if (known is null)
        {
            // No episode list: siblings are the only evidence. A single-season library is a
            // safe bet; otherwise leave it to the absolute-number search path.
            return siblingSeasons.Count == 1
                ? new EpisodeGuess(siblingSeasons[0], number, null, GuessConfidence.Low, $"The other files are from season {siblingSeasons[0]}")
                : null;
        }

        var fitting = episodesPerSeason!.Where(kv => number <= kv.Value).Select(kv => kv.Key).OrderBy(s => s).ToList();

        if (siblingSeasons.Count == 1 && fitting.Contains(siblingSeasons[0]))
            return new EpisodeGuess(siblingSeasons[0], number, null, GuessConfidence.High, $"The other files are from season {siblingSeasons[0]}");

        if (fitting.Count == 1)
            return new EpisodeGuess(fitting[0], number, null, GuessConfidence.High, $"Only season {fitting[0]} has an episode {number}");

        // Numbers running past every season, or past season 1 across the group, are absolute.
        if (fitting.Count == 0 || highestBareNumber > episodesPerSeason[fitting[0]])
        {
            if (number >= 1 && number <= known.Count)
            {
                var mapped = known[number - 1];
                return new EpisodeGuess(mapped.Season, mapped.Number, null, GuessConfidence.High, $"Episode {number} overall (absolute numbering)");
            }
            return null;
        }

        var first = fitting[0];
        return new EpisodeGuess(first, number, null, GuessConfidence.Low,
            $"Episode {number} exists in seasons {string.Join(", ", fitting)} — assuming season {first}; pick the season if this is wrong");
    }
}
