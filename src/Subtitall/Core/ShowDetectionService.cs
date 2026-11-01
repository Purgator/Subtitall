using System.Collections.Concurrent;

namespace Subtitall.Core;

public enum DetectionStatus
{
    /// <summary>One show clearly matches the filename title.</summary>
    Resolved,

    /// <summary>Several shows could be it — the user should pick one.</summary>
    Ambiguous,

    /// <summary>No metadata source knows a show by that name.</summary>
    NotFound,
}

/// <summary>A show with everything the app needs about it: other names and the episode list.</summary>
public sealed record ResolvedShow(TvShow Show, IReadOnlyList<string> Aliases, IReadOnlyList<TvEpisode> Episodes)
{
    /// <summary>Seasons the show has (specials excluded), ascending.</summary>
    public IReadOnlyList<int> Seasons => Episodes.Select(e => e.Season).Where(s => s > 0).Distinct().Order().ToList();
}

/// <summary>Outcome of matching one filename title against the metadata sources.</summary>
public sealed record ShowDetection(
    string Title, DetectionStatus Status, ResolvedShow? Show, IReadOnlyList<TvShow> Candidates, bool ChosenByUser = false);

/// <summary>
/// The one place that turns a filename title into a show. Both the subtitle search and the
/// rename feature go through it, so they always agree on what a file is. A title is searched on
/// every configured metadata source; a result whose name or alternate titles equal the filename
/// title wins outright, a lone fuzzy match is accepted, and anything less is reported as
/// ambiguous with the candidates so the user can pick. The user's picks are remembered for the
/// session, as are lookups (per title) and show details (per show).
/// </summary>
public sealed class ShowDetectionService(ShowMetadataService metadata, ILogger<ShowDetectionService> logger)
{
    private readonly ConcurrentDictionary<string, ShowDetection> _byTitle = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, TvShow> _choices = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Task<ResolvedShow>> _details = new();

    public async Task<ShowDetection> DetectAsync(string title, int? year, CancellationToken ct)
    {
        title = title.Trim();
        if (_choices.TryGetValue(title, out var chosen))
            return new ShowDetection(title, DetectionStatus.Resolved, await GetDetailsAsync(chosen, ct), [chosen], ChosenByUser: true);
        if (_byTitle.TryGetValue(title, out var cached))
            return cached;

        var results = await metadata.SearchShowsAsync(title, ct);
        var top = results.Take(4).ToList();

        // Alternate titles make romaji anime names ("Shingeki no Kyojin") match their show.
        var aliasTasks = top.Select(s => GuardedAliasesAsync(s, ct)).ToList();
        await Task.WhenAll(aliasTasks);
        var aliases = new Dictionary<string, IReadOnlyList<string>>();
        for (var i = 0; i < top.Count; i++)
            aliases[Key(top[i])] = aliasTasks[i].Result;

        var detection = Rank(title, year, results, aliases);
        if (detection.Status == DetectionStatus.Resolved)
            detection = detection with { Show = await GetDetailsAsync(detection.Candidates[0], ct) };

        // Only cache lookups that got an answer — an outage shouldn't stick for the whole session.
        if (results.Count > 0)
            _byTitle[title] = detection;
        return detection;
    }

    /// <summary>Remembers the user's pick for a title; later detections of that title use it.</summary>
    public void Choose(string title, TvShow show)
    {
        _choices[title.Trim()] = show;
        _byTitle.TryRemove(title.Trim(), out _);
    }

    /// <summary>Aliases and the full episode list of a show, fetched once per show.</summary>
    public async Task<ResolvedShow> GetDetailsAsync(TvShow show, CancellationToken ct)
    {
        var key = Key(show);
        var task = _details.GetOrAdd(key, _ => LoadDetailsAsync(show, ct));
        try
        {
            return await task;
        }
        catch
        {
            _details.TryRemove(key, out _); // Let a later call retry after a failure.
            throw;
        }
    }

    private async Task<ResolvedShow> LoadDetailsAsync(TvShow show, CancellationToken ct)
    {
        var episodesTask = metadata.GetEpisodesAsync(show.Source, show.Id, show.Name, ct);
        var aliasesTask = metadata.GetAliasesAsync(show.Source, show.Id, show.Name, ct);
        await Task.WhenAll(episodesTask, aliasesTask);
        return new ResolvedShow(show, aliasesTask.Result, episodesTask.Result);
    }

    private async Task<IReadOnlyList<string>> GuardedAliasesAsync(TvShow show, CancellationToken ct)
    {
        try
        {
            return (await GetDetailsAsync(show, ct)).Aliases;
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load details for {Show}", show.Name);
            return [];
        }
    }

    /// <summary>
    /// Picks the show for a title from search results. An exact name match wins (then an exact
    /// alias match; a year in the filename breaks ties between remakes). Failing that, fuzzy
    /// matches: one is accepted, and so is the source's top hit when the others are merely its
    /// spin-offs ("Attack on Titan: Junior High"); otherwise they are offered as candidates.
    /// With no match at all, the raw results are still offered so the user can pick one.
    /// </summary>
    internal static ShowDetection Rank(
        string title, int? year, IReadOnlyList<TvShow> results,
        IReadOnlyDictionary<string, IReadOnlyList<string>> aliasesByShow)
    {
        if (results.Count == 0)
            return new ShowDetection(title, DetectionStatus.NotFound, null, []);

        IReadOnlyList<string> NamesOf(TvShow show) =>
            [show.Name, .. aliasesByShow.GetValueOrDefault(Key(show)) ?? []];

        var normalizedTitle = Normalize(title);
        var exact = results.Where(s => Normalize(s.Name) == normalizedTitle).ToList();
        if (exact.Count == 0)
            exact = results.Where(s => NamesOf(s).Any(n => Normalize(n) == normalizedTitle)).ToList();
        if (exact.Count > 1 && year is int y)
        {
            var sameYear = exact.Where(s => s.Premiered?.StartsWith(y.ToString()) == true).ToList();
            if (sameYear.Count >= 1)
                exact = sameYear;
        }
        if (exact.Count == 1)
            return new ShowDetection(title, DetectionStatus.Resolved, null, exact);
        if (exact.Count > 1)
        {
            // The same show listed by two sources is one show, not a doubt.
            var distinct = exact.GroupBy(s => $"{Normalize(s.Name)}|{PremiereYear(s)}").Count();
            return distinct == 1
                ? new ShowDetection(title, DetectionStatus.Resolved, null, exact)
                : new ShowDetection(title, DetectionStatus.Ambiguous, null, exact);
        }

        var fuzzy = results.Where(s => TextMatch.MatchesShowName(title, NamesOf(s))).ToList();
        if (fuzzy.Count == 1)
            return new ShowDetection(title, DetectionStatus.Resolved, null, fuzzy);
        if (fuzzy.Count > 1)
        {
            // The source's own top hit, with the rest being its spin-offs, is not a doubt.
            var lead = fuzzy[0];
            var leadTokens = TextMatch.Tokenize(lead.Name);
            var othersAreSpinOffs = lead == results[0] &&
                fuzzy.Skip(1).All(s => leadTokens.IsSubsetOf(TextMatch.Tokenize(s.Name)));
            return othersAreSpinOffs
                ? new ShowDetection(title, DetectionStatus.Resolved, null, fuzzy)
                : new ShowDetection(title, DetectionStatus.Ambiguous, null, fuzzy);
        }

        return new ShowDetection(title, DetectionStatus.NotFound, null, results.Take(6).ToList());
    }

    internal static string Key(TvShow show) => $"{show.Source}:{show.Id}";

    private static string? PremiereYear(TvShow show) =>
        show.Premiered is { Length: >= 4 } p ? p[..4] : null;

    private static string Normalize(string name) => string.Join(' ', TextMatch.Tokenize(name).Order());
}
