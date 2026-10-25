using Subtitall.Core.Providers;

namespace Subtitall.Core;

public sealed record SearchOutcome(
    string ItemId, string Language, bool Downloaded, bool Skipped, string? Provider, string? SavedPath, string? Error);

/// <summary>Fans a search out to all configured providers, ranks results, downloads the best.</summary>
public sealed class SubtitleSearchService(
    IEnumerable<ISubtitleProvider> providers,
    SettingsService settings,
    ShowAliasResolver aliases,
    ProviderHealth health,
    ILogger<SubtitleSearchService> logger)
{
    /// <summary>Searches all configured providers; returns candidates ranked best-first.</summary>
    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        MediaItem item, string language, CancellationToken ct = default)
    {
        var config = settings.Current;
        var active = providers.Where(p => p.IsConfigured(config)).ToList();
        var skipped = active.Where(p => !health.IsAvailable(p.Name)).ToList();
        foreach (var provider in skipped)
            logger.LogInformation("{Provider} is cooling down after repeated failures — skipped", provider.Name);
        active = active.Except(skipped).ToList();

        // TV shows are often named differently across providers (romaji vs English anime
        // titles) — resolve the other known names once so providers can fall back to them.
        if (item.Parsed.IsEpisode && item.AlternateTitles is null)
            item.AlternateTitles = (await aliases.GetAlternateNamesAsync(item.Parsed.Title, ct)).ToList();

        var tasks = active.Select(async provider =>
        {
            try
            {
                var found = await provider.SearchAsync(item, language, config, ct);
                health.ReportSuccess(provider.Name);
                return found;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                health.ReportFailure(provider.Name);
                logger.LogWarning(ex, "{Provider} search failed for {File}", provider.Name, item.FileName);
                return (IReadOnlyList<SubtitleCandidate>)[];
            }
        });

        var results = (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();
        foreach (var candidate in results)
            candidate.Score = ComputeScore(candidate, item, health.SuccessRate(candidate.Provider));
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

        return await DownloadCandidateAsync(item, SelectDownloadOrder(candidates), language, ct);
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
                if (!SubtitleValidator.LooksLikeSubtitle(payload.Content, payload.Extension))
                    throw new InvalidOperationException("Downloaded file doesn't look like a subtitle");
                var path = SubtitleNaming.GetSubtitlePath(item.Path, language, payload.Extension);
                await File.WriteAllBytesAsync(path, payload.Content, ct);
                health.ReportSuccess(provider.Name);
                return new SearchOutcome(item.Id, language, true, false, provider.Name, path, null);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                health.ReportFailure(provider.Name);
                lastError = $"{candidate.Provider}: {ex.Message}";
                logger.LogWarning(ex, "Download from {Provider} failed for {File}", candidate.Provider, item.FileName);
            }
        }

        return new SearchOutcome(item.Id, language, false, false, null, null, lastError ?? "No subtitles found");
    }

    /// <summary>
    /// Picks the candidates worth a download attempt: best score first, but never more
    /// than two per provider so one bad provider can't exhaust every fallback slot.
    /// </summary>
    internal static List<SubtitleCandidate> SelectDownloadOrder(IReadOnlyList<SubtitleCandidate> ranked)
    {
        const int maxAttempts = 6, maxPerProvider = 2;
        var perProvider = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var selected = new List<SubtitleCandidate>();
        foreach (var candidate in ranked)
        {
            var used = perProvider.GetValueOrDefault(candidate.Provider);
            if (used >= maxPerProvider)
                continue;
            perProvider[candidate.Provider] = used + 1;
            selected.Add(candidate);
            if (selected.Count >= maxAttempts)
                break;
        }
        return selected;
    }

    internal static double ComputeScore(SubtitleCandidate candidate, MediaItem item, double providerSuccessRate = 1.0)
    {
        double score = 0;
        if (candidate.HashMatch)
            score += 100;

        score += 40 * TextMatch.Jaccard(candidate.ReleaseName, Path.GetFileNameWithoutExtension(item.FileName));

        if (item.Parsed.ReleaseGroup is { } group &&
            candidate.ReleaseName.Contains(group, StringComparison.OrdinalIgnoreCase))
            score += 15;

        score += Math.Min(10, Math.Log10(candidate.Downloads + 1) * 4);

        // Prefer regular subtitles over hearing-impaired ones when both exist.
        if (candidate.HearingImpaired)
            score -= 8;

        // Providers that have been failing recently rank a little lower.
        score += 5 * providerSuccessRate;

        return Math.Round(score, 1);
    }

}
