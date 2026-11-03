using System.Collections.Concurrent;

namespace Subtitall.Core;

/// <summary>Download quota as last reported by a provider (OpenSubtitles counts downloads per day).</summary>
public sealed record ProviderQuota(int? Remaining, DateTimeOffset? ResetAt)
{
    public bool Exhausted(DateTimeOffset now) => Remaining is <= 0 && (ResetAt is null || now < ResetAt);
}

/// <summary>
/// Tracks per-provider reliability: repeated failures put a provider in a short cooldown
/// (circuit breaker) so one dead service never slows every search, and the long-run success
/// rate feeds the candidate ranking. It also remembers each provider's download quota so a
/// used-up quota is reported as such instead of looking like "nothing found".
/// </summary>
public sealed class ProviderHealth(TimeProvider? time = null)
{
    private readonly TimeProvider _time = time ?? TimeProvider.System;

    private sealed record Stat(int ConsecutiveFailures, DateTimeOffset? RetryAt, int Successes, int Failures);

    private readonly ConcurrentDictionary<string, Stat> _stats = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, ProviderQuota> _quotas = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Failures in a row before the provider is put in cooldown.</summary>
    private const int FailuresBeforeCooldown = 2;

    private static readonly TimeSpan BaseCooldown = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MaxCooldown = TimeSpan.FromMinutes(10);

    /// <summary>False while the provider is cooling down after repeated failures.</summary>
    public bool IsAvailable(string provider) => CooldownUntil(provider) is null;

    /// <summary>When the current cooldown ends, or null when the provider is available.</summary>
    public DateTimeOffset? CooldownUntil(string provider) =>
        _stats.TryGetValue(provider, out var stat) && stat.RetryAt is { } retryAt && _time.GetUtcNow() < retryAt
            ? retryAt
            : null;

    public void ReportSuccess(string provider) =>
        _stats.AddOrUpdate(provider,
            _ => new Stat(0, null, 1, 0),
            (_, s) => s with { ConsecutiveFailures = 0, RetryAt = null, Successes = s.Successes + 1 });

    public void ReportFailure(string provider) =>
        _stats.AddOrUpdate(provider,
            _ => new Stat(1, null, 0, 1),
            (_, s) =>
            {
                var failures = s.ConsecutiveFailures + 1;
                DateTimeOffset? retryAt = null;
                if (failures >= FailuresBeforeCooldown)
                {
                    var cooldown = BaseCooldown * Math.Pow(2, failures - FailuresBeforeCooldown);
                    retryAt = _time.GetUtcNow() + (cooldown > MaxCooldown ? MaxCooldown : cooldown);
                }
                return s with { ConsecutiveFailures = failures, RetryAt = retryAt, Failures = s.Failures + 1 };
            });

    /// <summary>Records the quota a provider reported with its last download.</summary>
    public void ReportQuota(string provider, int? remaining, DateTimeOffset? resetAt) =>
        _quotas[provider] = new ProviderQuota(remaining, resetAt);

    /// <summary>Last known quota, or null when the provider never reported one.</summary>
    public ProviderQuota? Quota(string provider) =>
        _quotas.TryGetValue(provider, out var quota) ? quota : null;

    /// <summary>True while the provider's download quota is used up (it may still be searched).</summary>
    public bool IsQuotaExhausted(string provider) =>
        Quota(provider)?.Exhausted(_time.GetUtcNow()) == true;

    /// <summary>Long-run success ratio in [0, 1]; unknown providers start fully trusted.</summary>
    public double SuccessRate(string provider)
    {
        if (!_stats.TryGetValue(provider, out var stat))
            return 1.0;
        var total = stat.Successes + stat.Failures;
        // Laplace smoothing — a single early failure shouldn't tank a provider.
        return total == 0 ? 1.0 : (stat.Successes + 2.0) / (total + 2.0);
    }
}
