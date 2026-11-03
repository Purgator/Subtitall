using Subtitall.Core.Providers;

namespace Subtitall.Core;

/// <summary>What one source did during a search, so the user sees why nothing came back.</summary>
public enum SourceStatus
{
    /// <summary>Not enabled or missing its API key.</summary>
    Off,

    /// <summary>Can't search this kind of file (e.g. a TV-only source given a movie).</summary>
    NotApplicable,

    /// <summary>Skipped: cooling down after repeated failures.</summary>
    CoolingDown,

    /// <summary>Searched, nothing matched.</summary>
    Nothing,

    /// <summary>Searched and found candidates.</summary>
    Found,

    /// <summary>Found candidates, but every download attempt failed.</summary>
    DownloadFailed,

    /// <summary>Found candidates, but the daily download quota is used up.</summary>
    Quota,

    /// <summary>The search itself failed (network, API error, bad key).</summary>
    Error,
}

public sealed record SourceOutcome(string Provider, SourceStatus Status, int Found, string? Detail);

/// <summary>Ranked candidates plus what each source did.</summary>
public sealed record SearchReport(IReadOnlyList<SubtitleCandidate> Candidates, IReadOnlyList<SourceOutcome> Sources);

public sealed record SearchOutcome(
    string ItemId, string Language, bool Downloaded, bool Skipped, string? Provider, string? SavedPath, string? Error,
    IReadOnlyList<SourceOutcome> Sources)
{
    public SearchOutcome(string itemId, string language, bool downloaded, bool skipped, string? provider, string? savedPath, string? error)
        : this(itemId, language, downloaded, skipped, provider, savedPath, error, [])
    {
    }
}

/// <summary>Fans a search out to all configured providers, ranks results, downloads the best.</summary>
public sealed class SubtitleSearchService(
    IEnumerable<ISubtitleProvider> providers,
    SettingsService settings,
    ProviderHealth health,
    ILogger<SubtitleSearchService> logger)
{
    /// <summary>Searches all configured providers; returns candidates ranked best-first and a per-source report.</summary>
    public async Task<SearchReport> SearchAsync(MediaItem item, string language, CancellationToken ct = default)
    {
        var config = settings.Current;
        var outcomes = new Dictionary<string, SourceOutcome>(StringComparer.OrdinalIgnoreCase);
        var active = new List<ISubtitleProvider>();

        foreach (var provider in providers)
        {
            if (!provider.IsConfigured(config))
                outcomes[provider.Name] = new SourceOutcome(provider.Name, SourceStatus.Off, 0, "not enabled or no API key");
            else if (provider.CannotSearchReason(item) is { } reason)
                outcomes[provider.Name] = new SourceOutcome(provider.Name, SourceStatus.NotApplicable, 0, reason);
            else if (health.CooldownUntil(provider.Name) is { } until)
            {
                var seconds = Math.Max(1, (int)(until - DateTimeOffset.UtcNow).TotalSeconds);
                outcomes[provider.Name] = new SourceOutcome(provider.Name, SourceStatus.CoolingDown, 0,
                    $"skipped — failed repeatedly, retrying in {seconds}s");
                logger.LogInformation("{Provider} is cooling down after repeated failures — skipped", provider.Name);
            }
            else
                active.Add(provider);
        }

        // Show detection (name, aliases, season guess) has already run for the item — see
        // MediaDetectionService; providers read item.Effective and item.AlternateTitles.
        var tasks = active.Select(async provider =>
        {
            try
            {
                var found = await provider.SearchAsync(item, language, config, ct);
                health.ReportSuccess(provider.Name);
                outcomes[provider.Name] = found.Count > 0
                    ? new SourceOutcome(provider.Name, SourceStatus.Found, found.Count, $"{found.Count} found")
                    : new SourceOutcome(provider.Name, SourceStatus.Nothing, 0, "nothing for this file in this language");
                return found;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                health.ReportFailure(provider.Name);
                outcomes[provider.Name] = new SourceOutcome(provider.Name, SourceStatus.Error, 0, ex.Message);
                logger.LogWarning(ex, "{Provider} search failed for {File}", provider.Name, item.FileName);
                return (IReadOnlyList<SubtitleCandidate>)[];
            }
        });

        var results = (await Task.WhenAll(tasks)).SelectMany(r => r).ToList();
        foreach (var candidate in results)
            candidate.Score = ComputeScore(candidate, item, health.SuccessRate(candidate.Provider));
        return new SearchReport(results.OrderByDescending(c => c.Score).ToList(), OrderedOutcomes(outcomes));
    }

    /// <summary>Downloads the best candidate for one video + language and saves it next to the video.</summary>
    public async Task<SearchOutcome> DownloadBestAsync(MediaItem item, string language, CancellationToken ct = default)
    {
        var config = settings.Current;
        if (!config.OverwriteExisting && SubtitleNaming.HasSubtitle(item.Path, language))
            return new SearchOutcome(item.Id, language, false, true, null, null, null);

        var report = await SearchAsync(item, language, ct);
        if (report.Candidates.Count == 0)
        {
            var reason = providers.Any(p => p.IsConfigured(config))
                ? "No subtitles found"
                : "No provider is configured — add an API key or enable a provider in Settings";
            return new SearchOutcome(item.Id, language, false, false, null, null, reason, report.Sources);
        }

        return await DownloadCandidateAsync(item, SelectDownloadOrder(report.Candidates), language, report.Sources, ct);
    }

    /// <summary>Downloads one specific candidate chosen by the user.</summary>
    public Task<SearchOutcome> DownloadAsync(
        MediaItem item, SubtitleCandidate candidate, CancellationToken ct = default) =>
        DownloadCandidateAsync(item, [candidate], candidate.Language, [], ct);

    private async Task<SearchOutcome> DownloadCandidateAsync(
        MediaItem item, IReadOnlyList<SubtitleCandidate> ranked, string language,
        IReadOnlyList<SourceOutcome> searchOutcomes, CancellationToken ct)
    {
        var config = settings.Current;
        var outcomes = searchOutcomes.ToDictionary(o => o.Provider, StringComparer.OrdinalIgnoreCase);
        string? lastError = null;

        foreach (var candidate in ranked)
        {
            var provider = providers.FirstOrDefault(p => p.Name == candidate.Provider);
            if (provider is null)
                continue;
            if (health.IsQuotaExhausted(provider.Name))
            {
                // The quota blocks every candidate of this provider — don't burn attempts on them.
                var quota = health.Quota(provider.Name);
                lastError = $"{provider.Name}: daily download quota used up{ResetText(quota)}";
                outcomes[provider.Name] = new SourceOutcome(provider.Name, SourceStatus.Quota, Found(provider.Name),
                    $"download quota used up{ResetText(quota)}");
                continue;
            }
            try
            {
                var payload = await provider.DownloadAsync(candidate, config, ct);
                if (!SubtitleValidator.LooksLikeSubtitle(payload.Content, payload.Extension))
                    throw new InvalidOperationException("Downloaded file doesn't look like a subtitle");
                var path = SubtitleNaming.GetSubtitlePath(item.Path, language, payload.Extension);
                await File.WriteAllBytesAsync(path, payload.Content, ct);
                health.ReportSuccess(provider.Name);
                outcomes[provider.Name] = new SourceOutcome(provider.Name, SourceStatus.Found, Found(provider.Name),
                    $"downloaded “{candidate.ReleaseName}”{QuotaText(provider.Name)}");
                return new SearchOutcome(item.Id, language, true, false, provider.Name, path, null, OrderedOutcomes(outcomes));
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (QuotaExceededException ex)
            {
                lastError = $"{candidate.Provider}: {ex.Message}";
                outcomes[provider.Name] = new SourceOutcome(provider.Name, SourceStatus.Quota, Found(provider.Name), ex.Message);
                logger.LogInformation("{Provider} download quota used up", provider.Name);
            }
            catch (Exception ex)
            {
                health.ReportFailure(provider.Name);
                lastError = $"{candidate.Provider}: {ex.Message}";
                var previous = outcomes.GetValueOrDefault(provider.Name);
                outcomes[provider.Name] = new SourceOutcome(provider.Name, SourceStatus.DownloadFailed, Found(provider.Name),
                    previous?.Status == SourceStatus.DownloadFailed ? $"{previous.Detail}; {ex.Message}" : $"download failed: {ex.Message}");
                logger.LogWarning(ex, "Download from {Provider} failed for {File}", candidate.Provider, item.FileName);
            }
        }

        return new SearchOutcome(item.Id, language, false, false, null, null, lastError ?? "No subtitles found", OrderedOutcomes(outcomes));

        int Found(string provider) => outcomes.GetValueOrDefault(provider)?.Found ?? 0;
    }

    private string QuotaText(string provider) =>
        health.Quota(provider) is { Remaining: int left } ? $" — {left} download{(left == 1 ? "" : "s")} left today" : "";

    private static string ResetText(ProviderQuota? quota) =>
        quota?.ResetAt is { } at ? $" — resets {at.ToLocalTime():HH:mm}" : "";

    /// <summary>Sources in the configured provider order, so the list reads the same everywhere.</summary>
    private IReadOnlyList<SourceOutcome> OrderedOutcomes(Dictionary<string, SourceOutcome> outcomes) =>
        providers.Select(p => outcomes.GetValueOrDefault(p.Name)).OfType<SourceOutcome>().ToList();

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
