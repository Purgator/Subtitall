using System.Collections.Concurrent;
using System.Text.Json;

namespace Subtitall.Core.Providers;

/// <summary>
/// Gestdown (api.gestdown.info) — keyless Addic7ed proxy for TV shows.
/// HTTP 423 means the show is being indexed server-side; retry shortly.
/// </summary>
public sealed class GestdownProvider(IHttpClientFactory httpFactory) : ISubtitleProvider
{
    public const string ProviderName = "Gestdown";
    private const string BaseUrl = "https://api.gestdown.info";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // Sustained limit is ~50 req/min per IP.
    private readonly RateGate _gate = new(TimeSpan.FromMilliseconds(1300));
    private readonly ConcurrentDictionary<string, string?> _showIdCache = new(StringComparer.OrdinalIgnoreCase);

    public string Name => ProviderName;

    public bool IsConfigured(AppSettings settings) => settings.Gestdown.Enabled;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        MediaItem item, string language, AppSettings settings, CancellationToken ct)
    {
        var parsed = item.Parsed;
        if (parsed.Season is not { } season || parsed.Episode is not { } episode)
            return []; // TV shows only, and absolute anime numbering can't be queried here.

        var showId = await GetShowIdAsync(parsed.Title, ct);
        if (showId is null)
            return [];

        var url = $"{BaseUrl}/subtitles/get/{showId}/{season}/{episode}/{ToGestdownLanguage(language)}";
        var response = await GetAsync(url, ct, retryOn423: true);
        if (response is null)
            return [];

        var result = JsonSerializer.Deserialize<SubtitlesResponse>(response, Json);
        var candidates = new List<SubtitleCandidate>();
        foreach (var sub in result?.MatchingSubtitles ?? [])
        {
            if (sub.Completed != true || sub.DownloadUri is null)
                continue;
            candidates.Add(new SubtitleCandidate
            {
                Provider = ProviderName,
                DownloadRef = sub.DownloadUri,
                Language = language,
                ReleaseName = $"{sub.Version} {string.Join(' ', sub.Qualities ?? [])}".Trim(),
                Downloads = sub.DownloadCount ?? 0,
            });
        }
        return candidates;
    }

    public async Task<SubtitlePayload> DownloadAsync(
        SubtitleCandidate candidate, AppSettings settings, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        var http = httpFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, BaseUrl + candidate.DownloadRef);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gestdown download failed ({(int)response.StatusCode})");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);
        return new SubtitlePayload(bytes, ".srt");
    }

    private async Task<string?> GetShowIdAsync(string title, CancellationToken ct)
    {
        if (_showIdCache.TryGetValue(title, out var cached))
            return cached;

        var response = await GetAsync($"{BaseUrl}/shows/search/{Uri.EscapeDataString(title)}", ct, retryOn423: false);
        string? id = null;
        if (response is not null)
        {
            var result = JsonSerializer.Deserialize<ShowSearchResponse>(response, Json);
            var shows = result?.Shows ?? [];
            var show = shows.FirstOrDefault(s => string.Equals(s.Name, title, StringComparison.OrdinalIgnoreCase))
                       ?? shows.FirstOrDefault();
            id = show?.Id;
        }
        _showIdCache[title] = id;
        return id;
    }

    /// <summary>GET returning null on 404 (not found is a normal outcome, not an error).</summary>
    private async Task<string?> GetAsync(string url, CancellationToken ct, bool retryOn423)
    {
        await _gate.WaitAsync(ct);
        var http = httpFactory.CreateClient();
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);
        using var response = await http.SendAsync(request, ct);

        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return null;
        if (response.StatusCode == System.Net.HttpStatusCode.Locked)
        {
            if (!retryOn423)
                return null;
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            return await GetAsync(url, ct, retryOn423: false)
                ?? throw new InvalidOperationException("Gestdown is still indexing this show — try again in a minute");
        }
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"Gestdown error {(int)response.StatusCode}");
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Gestdown accepts 2-letter ISO codes (and pt-BR); 3-letter codes silently miss.</summary>
    private static string ToGestdownLanguage(string code) => code switch
    {
        "pt-br" => "pt-BR",
        "pt-pt" => "pt",
        "zh-cn" or "zh-tw" => "zh",
        _ => code.Length > 2 ? code[..2] : code,
    };

    private sealed class ShowSearchResponse
    {
        public List<ShowDto>? Shows { get; set; }
    }

    private sealed class ShowDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
    }

    private sealed class SubtitlesResponse
    {
        public List<SubtitleDto>? MatchingSubtitles { get; set; }
    }

    private sealed class SubtitleDto
    {
        public string? SubtitleId { get; set; }
        public string? Version { get; set; }
        public bool? Completed { get; set; }
        public string? DownloadUri { get; set; }
        public int? DownloadCount { get; set; }
        public List<string>? Qualities { get; set; }
    }
}
