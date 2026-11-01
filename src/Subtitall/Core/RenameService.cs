using System.Text.RegularExpressions;

namespace Subtitall.Core;

public sealed record RenameProposal(string ItemId, string OldName, string NewName, string? Problem)
{
    public bool CanApply => Problem is null && !string.Equals(OldName, NewName, StringComparison.Ordinal);

    /// <summary>Why the season was chosen when the filename didn't spell it out.</summary>
    public string? Note { get; init; }
}

public sealed record RenameOutcome(string ItemId, bool Renamed, string? Error);

/// <summary>
/// Builds and applies episode rename proposals from the metadata sources' episode data. Files
/// are matched to episodes the same way the subtitle search sees them (<see cref="EpisodeGuesser"/>).
/// </summary>
public sealed class RenameService(LibraryService library)
{
    /// <summary>Proposes "{Show} - S01E02 - Title.ext" names for the given items.</summary>
    public IReadOnlyList<RenameProposal> BuildProposals(
        IReadOnlyList<MediaItem> items,
        string showName,
        IReadOnlyList<TvEpisode> episodes,
        string template,
        IReadOnlyList<string>? alternateNames = null)
    {
        // Files are matched against every name the show is known under — the TVMaze
        // title plus its akas (romaji anime names, translations) and their acronyms.
        var knownNames = new List<string> { showName };
        if (alternateNames is not null)
            knownNames.AddRange(alternateNames);

        var bySeasonEpisode = episodes
            .Where(e => e.Season > 0)
            .GroupBy(e => (e.Season, e.Number))
            .ToDictionary(g => g.Key, g => g.First());

        // Guard against renaming files that belong to a different show
        // (e.g. an anime with absolute numbering sitting in the same list).
        var matching = items.Where(i => TextMatch.MatchesShowName(i.Parsed.Title, knownNames)).ToList();
        var guesses = EpisodeGuesser.Guess(
            matching.Select(i => new GuessInput(i.Parsed, i.SeasonOverride)).ToList(), episodes);
        var guessById = matching.Zip(guesses).ToDictionary(pair => pair.First.Id, pair => pair.Second);

        var proposals = new List<RenameProposal>();
        foreach (var item in items)
        {
            var parsed = item.Parsed;

            if (!guessById.TryGetValue(item.Id, out var guess))
            {
                proposals.Add(new RenameProposal(item.Id, item.FileName, item.FileName,
                    $"Doesn't look like an episode of “{showName}”"));
                continue;
            }

            if (guess is null)
            {
                proposals.Add(new RenameProposal(item.Id, item.FileName, item.FileName,
                    parsed.AbsoluteEpisode.HasValue
                        ? $"Episode {parsed.AbsoluteEpisode} doesn't fit any season — pick the season"
                        : "No season/episode detected in the filename"));
                continue;
            }

            int season = guess.Season, number = guess.Episode;
            bySeasonEpisode.TryGetValue((season, number), out var episode);
            var note = guess.Confidence == GuessConfidence.Explicit ? null : guess.Reason;

            // Multi-episode files ("S01E01E02") keep both markers and join the titles.
            var episodeText = number.ToString("00");
            var title = episode?.Name ?? "";
            if (guess.EndEpisode is int endNumber && endNumber > number)
            {
                episodeText = $"{number:00}-E{endNumber:00}";
                if (bySeasonEpisode.TryGetValue((season, endNumber), out var lastEpisode) &&
                    lastEpisode.Name.Length > 0)
                    title = title.Length > 0 ? $"{title} & {lastEpisode.Name}" : lastEpisode.Name;
            }

            var extension = Path.GetExtension(item.Path);
            var newName = ApplyTemplate(template, showName, season, episodeText, title) + extension;
            newName = SanitizeFileName(newName);

            string? problem = null;
            if (episode is null)
                problem = $"Episode S{season:00}E{number:00} not found on the metadata sources";
            else
            {
                var newPath = Path.Combine(Path.GetDirectoryName(item.Path) ?? "", newName);
                if (!newPath.Equals(item.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(newPath))
                    problem = "A file with the new name already exists";
            }

            proposals.Add(new RenameProposal(item.Id, item.FileName, newName, problem) { Note = note });
        }
        return FlagDuplicateTargets(proposals);
    }

    /// <summary>Two proposals resolving to the same path would collide at apply time — flag them.</summary>
    private IReadOnlyList<RenameProposal> FlagDuplicateTargets(List<RenameProposal> proposals)
    {
        string TargetKey(RenameProposal p)
        {
            var directory = Path.GetDirectoryName(library.Get(p.ItemId)?.Path ?? "") ?? "";
            return $"{directory}|{p.NewName}".ToLowerInvariant();
        }

        var duplicates = proposals
            .Where(p => p.CanApply)
            .GroupBy(TargetKey)
            .Where(g => g.Count() > 1)
            .SelectMany(g => g)
            .Select(p => p.ItemId)
            .ToHashSet();

        return proposals
            .Select(p => duplicates.Contains(p.ItemId)
                ? p with { Problem = "Several files would get this exact name" }
                : p)
            .ToList();
    }

    /// <summary>Applies confirmed proposals: renames the video and its companion subtitle files.</summary>
    public IReadOnlyList<RenameOutcome> Apply(IReadOnlyList<RenameProposal> proposals)
    {
        var outcomes = new List<RenameOutcome>();
        foreach (var proposal in proposals)
        {
            var item = library.Get(proposal.ItemId);
            if (item is null)
            {
                outcomes.Add(new RenameOutcome(proposal.ItemId, false, "File is no longer in the list"));
                continue;
            }
            if (!proposal.CanApply)
            {
                outcomes.Add(new RenameOutcome(proposal.ItemId, false, proposal.Problem ?? "Nothing to rename"));
                continue;
            }

            var directory = Path.GetDirectoryName(item.Path) ?? "";
            var newPath = Path.Combine(directory, proposal.NewName);
            try
            {
                var oldBase = Path.GetFileNameWithoutExtension(item.Path);
                var newBase = Path.GetFileNameWithoutExtension(proposal.NewName);
                var companions = SubtitleNaming.FindCompanionFiles(item.Path).ToList();

                File.Move(item.Path, newPath);
                library.UpdatePath(item, newPath);

                // "old.en.srt" → "new.en.srt": swap the base name, keep the language/ext suffix.
                // The video is already renamed at this point, so companion trouble is reported
                // as a warning on a successful outcome instead of a bogus failure.
                var warnings = new List<string>();
                foreach (var companion in companions)
                {
                    var companionName = Path.GetFileName(companion);
                    var target = Path.Combine(directory, newBase + companionName[oldBase.Length..]);
                    try
                    {
                        if (File.Exists(target))
                            warnings.Add($"subtitle \"{companionName}\" kept its name (a file with the new name already exists)");
                        else
                            File.Move(companion, target);
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        warnings.Add($"subtitle \"{companionName}\" could not be renamed: {ex.Message}");
                    }
                }

                outcomes.Add(new RenameOutcome(proposal.ItemId, true,
                    warnings.Count > 0 ? "Video renamed, but " + string.Join("; ", warnings) : null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcomes.Add(new RenameOutcome(proposal.ItemId, false, ex.Message));
            }
        }
        return outcomes;
    }

    private static string ApplyTemplate(string template, string show, int season, string episodeText, string title)
    {
        var result = template
            .Replace("{Show}", show, StringComparison.OrdinalIgnoreCase)
            .Replace("{Season}", season.ToString("00"), StringComparison.OrdinalIgnoreCase)
            .Replace("{Episode}", episodeText, StringComparison.OrdinalIgnoreCase)
            .Replace("{Title}", title, StringComparison.OrdinalIgnoreCase);
        // Tidy leftovers when the episode has no title: "… - " at the end.
        result = Regex.Replace(result, @"[ \-]+$", "");
        return result;
    }

    internal static string SanitizeFileName(string name)
    {
        name = string.Join("", name.Split(Path.GetInvalidFileNameChars()));
        name = Regex.Replace(name, @"\s+", " ").Trim();
        return name.TrimEnd('.');
    }
}
