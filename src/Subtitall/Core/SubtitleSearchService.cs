using System.Text.RegularExpressions;
using Subtitall.Core.Providers;

namespace Subtitall.Core;

public sealed record SearchOutcome(
    string ItemId, string Language, bool Downloaded, bool Skipped, string? Provider, string? SavedPath, string? Error);

/// <summary>Fans a search out to all configured providers, ranks results, downloads the best.</summary>
public sealed class SubtitleSearchService(
    IEnumerable<ISubtitleProvider> providers,
    SettingsService settings,
    ILogger<SubtitleSearchService> logger)
{
    /// <summary>Searches all configured providers; returns candidates ranked best-first.</summary>
    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        MediaItem item, string language, CancellationToken ct = default)
    {
        var config = settings.Current;
        var active = providers.Where(p => p.IsConfigured(config)).ToList();

        var tasks = active.Select(async provider =>
        {
            try
            {
                return await provider.SearchAsync(item, language, config, ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "{Provider} search failed for {File}", provider.Name, item.FileName);
                return (IReadOnlyList<SubtitleCandidate>)[];
            }
        });

        var results = (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();
        foreach (var candidate in results)
            candidate.Score = ComputeScore(candidate, item);
        return results.OrderByDescending(c => c.Score).ToList();
    }

    /// <summary>Downloads the best candidate for one video + language and saves it next to the video.</summary>
    public async Task<SearchOutcome> DownloadBestAsync(MediaItem item, string language, CancellationToken ct = default)
    {
        var config = settings.Current;
        if (!config.OverwriteExisting && SubtitleNaming.HasSubtitle(item.Path, language))
            return new SearchOutcome(item.Id, language, false, true, null, null, null);

        var candidates = await SearchAsync(item, language, ct);
        if (candidates.Count == 0)
        {
            var reason = providers.Any(p => p.IsConfigured(config))
                ? "No subtitles found"
                : "No provider is configured — add an API key or enable a provider in Settings";
            return new SearchOutcome(item.Id, language, false, false, null, null, reason);
        }

        return await DownloadCandidateAsync(item, candidates.Take(4).ToList(), language, ct);
    }

    /// <summary>Downloads one specific candidate chosen by the user.</summary>
    public Task<SearchOutcome> DownloadAsync(
        MediaItem item, SubtitleCandidate candidate, CancellationToken ct = default) =>
        DownloadCandidateAsync(item, [candidate], candidate.Language, ct);

    private async Task<SearchOutcome> DownloadCandidateAsync(
        MediaItem item, IReadOnlyList<SubtitleCandidate> ranked, string language, CancellationToken ct)
    {
        var config = settings.Current;
        string? lastError = null;

        foreach (var candidate in ranked)
        {
            var provider = providers.FirstOrDefault(p => p.Name == candidate.Provider);
            if (provider is null)
                continue;
            try
            {
                var payload = await provider.DownloadAsync(candidate, config, ct);
                var path = SubtitleNaming.GetSubtitlePath(item.Path, language, payload.Extension);
                await File.WriteAllBytesAsync(path, payload.Content, ct);
                return new SearchOutcome(item.Id, language, true, false, provider.Name, path, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = $"{candidate.Provider}: {ex.Message}";
                logger.LogWarning(ex, "Download from {Provider} failed for {File}", candidate.Provider, item.FileName);
            }
        }

        return new SearchOutcome(item.Id, language, false, false, null, null, lastError ?? "No subtitles found");
    }

    private static double ComputeScore(SubtitleCandidate candidate, MediaItem item)
    {
        double score = 0;
        if (candidate.HashMatch)
            score += 100;

        score += 40 * TokenSimilarity(candidate.ReleaseName, Path.GetFileNameWithoutExtension(item.FileName));

        if (item.Parsed.ReleaseGroup is { } group &&
            candidate.ReleaseName.Contains(group, StringComparison.OrdinalIgnoreCase))
            score += 15;

        score += Math.Min(10, Math.Log10(candidate.Downloads + 1) * 4);
        return Math.Round(score, 1);
    }

    private static double TokenSimilarity(string a, string b)
    {
        var setA = Tokenize(a);
        var setB = Tokenize(b);
        if (setA.Count == 0 || setB.Count == 0)
            return 0;
        var intersection = setA.Intersect(setB).Count();
        var union = setA.Union(setB).Count();
        return (double)intersection / union;
    }

    private static HashSet<string> Tokenize(string s) =>
        Regex.Split(s.ToLowerInvariant(), @"[^a-z0-9]+")
            .Where(t => t.Length > 1)
            .ToHashSet();
}
