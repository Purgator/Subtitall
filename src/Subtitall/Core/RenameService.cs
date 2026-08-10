using System.Text.RegularExpressions;

namespace Subtitall.Core;

public sealed record RenameProposal(string ItemId, string OldName, string NewName, string? Problem)
{
    public bool CanApply => Problem is null && !string.Equals(OldName, NewName, StringComparison.Ordinal);
}

public sealed record RenameOutcome(string ItemId, bool Renamed, string? Error);

/// <summary>Builds and applies episode rename proposals from TVMaze episode data.</summary>
public sealed class RenameService(LibraryService library)
{
    /// <summary>Proposes "{Show} - S01E02 - Title.ext" names for the given items.</summary>
    public IReadOnlyList<RenameProposal> BuildProposals(
        IReadOnlyList<MediaItem> items,
        string showName,
        IReadOnlyList<TvEpisode> episodes,
        string template)
    {
        // Absolute (anime) numbering maps onto regular episodes ordered by season/number.
        var ordered = episodes
            .Where(e => e.Season > 0)
            .OrderBy(e => e.Season).ThenBy(e => e.Number)
            .ToList();
        var bySeasonEpisode = ordered
            .GroupBy(e => (e.Season, e.Number))
            .ToDictionary(g => g.Key, g => g.First());

        var proposals = new List<RenameProposal>();
        foreach (var item in items)
        {
            var parsed = item.Parsed;

            // Guard against renaming files that belong to a different show
            // (e.g. an anime with absolute numbering sitting in the same list).
            if (TextMatch.Containment(parsed.Title, showName) < 0.6)
            {
                proposals.Add(new RenameProposal(item.Id, item.FileName, item.FileName,
                    $"Doesn't look like an episode of “{showName}”"));
                continue;
            }

            TvEpisode? episode = null;
            int? season = parsed.Season, number = parsed.Episode;

            if (season.HasValue && number.HasValue)
            {
                bySeasonEpisode.TryGetValue((season.Value, number.Value), out episode);
            }
            else if (parsed.AbsoluteEpisode is int abs && abs >= 1 && abs <= ordered.Count)
            {
                episode = ordered[abs - 1];
                season = episode.Season;
                number = episode.Number;
            }

            if (!season.HasValue || !number.HasValue)
            {
                proposals.Add(new RenameProposal(item.Id, item.FileName, item.FileName,
                    "No season/episode detected in the filename"));
                continue;
            }

            var extension = Path.GetExtension(item.Path);
            var newName = ApplyTemplate(template, showName, season.Value, number.Value, episode?.Name ?? "") + extension;
            newName = SanitizeFileName(newName);

            string? problem = null;
            if (episode is null)
                problem = $"Episode S{season:00}E{number:00} not found on TVMaze";
            else
            {
                var newPath = Path.Combine(Path.GetDirectoryName(item.Path) ?? "", newName);
                if (!newPath.Equals(item.Path, StringComparison.OrdinalIgnoreCase) && File.Exists(newPath))
                    problem = "A file with the new name already exists";
            }

            proposals.Add(new RenameProposal(item.Id, item.FileName, newName, problem));
        }
        return proposals;
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
                foreach (var companion in companions)
                {
                    var suffix = Path.GetFileName(companion)[oldBase.Length..];
                    var target = Path.Combine(directory, newBase + suffix);
                    if (!File.Exists(target))
                        File.Move(companion, target);
                }

                outcomes.Add(new RenameOutcome(proposal.ItemId, true, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcomes.Add(new RenameOutcome(proposal.ItemId, false, ex.Message));
            }
        }
        return outcomes;
    }

    private static string ApplyTemplate(string template, string show, int season, int episode, string title)
    {
        var result = template
            .Replace("{Show}", show, StringComparison.OrdinalIgnoreCase)
            .Replace("{Season}", season.ToString("00"), StringComparison.OrdinalIgnoreCase)
            .Replace("{Episode}", episode.ToString("00"), StringComparison.OrdinalIgnoreCase)
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
