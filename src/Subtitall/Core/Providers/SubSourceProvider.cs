using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Subtitall.Core.Providers;

/// <summary>
/// SubSource API (api.subsource.net v1) — free API key, hosts the former Subscene archive.
/// Free tier: 60 req/min. Title ids are per show-season for TV; downloads are ZIPs or raw files.
/// </summary>
public sealed class SubSourceProvider(IHttpClientFactory httpFactory) : ISubtitleProvider
{
    public const string ProviderName = "SubSource";
    private const string BaseUrl = "https://api.subsource.net/api/v1";

    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    // Free tier allows 60 req/min — stay just under it.
    private readonly RateGate _gate = new(TimeSpan.FromMilliseconds(1100));

    // Title lookups repeat heavily during batch searches; misses expire so new uploads appear.
    private static readonly TimeSpan NegativeCacheTtl = TimeSpan.FromMinutes(5);
    private readonly ConcurrentDictionary<string, (string? Id, DateTime AtUtc)> _titleCache = new(StringComparer.OrdinalIgnoreCase);

    public string Name => ProviderName;

    public bool IsConfigured(AppSettings settings) =>
        settings.SubSource.Enabled && settings.SubSource.ApiKey.Trim().Length > 0;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        MediaItem item, string language, AppSettings settings, CancellationToken ct)
    {
        var parsed = item.Parsed;
        if (parsed.IsEpisode && (!parsed.Season.HasValue || !parsed.Episode.HasValue))
            return []; // Absolute anime numbering can't be queried here.

        var movieId = await FindTitleIdAsync(parsed.Title, parsed, settings, ct);
        if (movieId is null && item.AlternateTitles is { Count: > 0 } alternates)
        {
            // The archive mostly indexes English titles — try the other known names.
            foreach (var alternate in alternates.Where(a => a.Any(char.IsAsciiLetter)).Take(3))
            {
                movieId = await FindTitleIdAsync(alternate, parsed, settings, ct);
                if (movieId is not null)
                    break;
            }
        }
        if (movieId is null)
            return [];

        var url = $"{BaseUrl}/subtitles?language={Uri.EscapeDataString(Languages.ToSubSource(language))}" +
                  $"&limit=100&movieId={Uri.EscapeDataString(movieId)}";
        if (parsed.Season.HasValue && parsed.Episode.HasValue)
            url += $"&seasonNumber={parsed.Season}&episodeNumber={parsed.Episode}";

        var response = await GetAsync(url, settings, ct);
        var listing = JsonSerializer.Deserialize<ListingResponse>(response, Json);

        var candidates = new List<SubtitleCandidate>();
        foreach (var sub in listing?.Data ?? [])
        {
            if (sub.SubtitleId is null)
                continue;
            candidates.Add(new SubtitleCandidate
            {
                Provider = ProviderName,
                DownloadRef = sub.SubtitleId,
                Language = language,
                ReleaseName = sub.ReleaseInfo?.FirstOrDefault() ?? "",
                HearingImpaired = sub.HearingImpaired ?? false,
            });
        }
        return candidates;
    }

    public async Task<SubtitlePayload> DownloadAsync(
        SubtitleCandidate candidate, AppSettings settings, CancellationToken ct)
    {
        var url = $"{BaseUrl}/subtitles/{Uri.EscapeDataString(candidate.DownloadRef)}/download";
        await _gate.WaitAsync(ct);
        using var request = BuildRequest(url, settings);
        var http = httpFactory.CreateClient();
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SubSource download failed ({(int)response.StatusCode})");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);

        if (bytes.Length > 3 && bytes[0] == 'P' && bytes[1] == 'K')
            return ExtractFromZip(bytes);
        if (bytes.Length > 4 && bytes[0] == 'R' && bytes[1] == 'a' && bytes[2] == 'r' && bytes[3] == '!')
            throw new InvalidOperationException("SubSource returned a RAR archive, which is not supported");
        return new SubtitlePayload(bytes, ".srt");
    }

    private async Task<string?> FindTitleIdAsync(
        string title, ParsedMedia parsed, AppSettings settings, CancellationToken ct)
    {
        // TV titles resolve per season on SubSource, so the cache key includes it.
        var cacheKey = $"{title}|{parsed.Season}|{parsed.Year}";
        if (_titleCache.TryGetValue(cacheKey, out var cached) &&
            (cached.Id is not null || DateTime.UtcNow - cached.AtUtc < NegativeCacheTtl))
            return cached.Id;

        var url = $"{BaseUrl}/movies/search?searchType=text&q={Uri.EscapeDataString(title.ToLowerInvariant())}";
        if (parsed.IsEpisode && parsed.Season is { } season)
            url += $"&season={season}";

        var response = await GetAsync(url, settings, ct);
        var result = JsonSerializer.Deserialize<TitleSearchResponse>(response, Json);
        var titles = (result?.Data ?? []).Where(t => t.MovieId is not null).ToList();

        // Prefer an exact title (or alternate-title) match; for movies also prefer the right year.
        var best = titles.FirstOrDefault(t => TitleMatches(t, title) && YearMatches(t, parsed))
                   ?? titles.FirstOrDefault(t => TitleMatches(t, title))
                   ?? (parsed.IsEpisode ? titles.FirstOrDefault() : titles.FirstOrDefault(t => YearMatches(t, parsed)))
                   ?? titles.FirstOrDefault();

        _titleCache[cacheKey] = (best?.MovieId, DateTime.UtcNow);
        return best?.MovieId;
    }

    private static bool TitleMatches(TitleDto entry, string title) =>
        string.Equals(entry.Title, title, StringComparison.OrdinalIgnoreCase) ||
        string.Equals(entry.AlternateTitle, title, StringComparison.OrdinalIgnoreCase) ||
        TextMatch.Jaccard(entry.Title ?? "", title) > 0.8;

    private static bool YearMatches(TitleDto entry, ParsedMedia parsed) =>
        parsed.IsEpisode || !parsed.Year.HasValue ||
        (int.TryParse(entry.ReleaseYear, out var year) && Math.Abs(year - parsed.Year.Value) <= 1);

    private async Task<string> GetAsync(string url, AppSettings settings, CancellationToken ct, bool allowRetry = true)
    {
        await _gate.WaitAsync(ct);
        using var request = BuildRequest(url, settings);
        var http = httpFactory.CreateClient();
        using var response = await http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && allowRetry)
        {
            await Task.Delay(TimeSpan.FromSeconds(3), ct);
            return await GetAsync(url, settings, ct, allowRetry: false);
        }
        if (response.StatusCode is System.Net.HttpStatusCode.Unauthorized or System.Net.HttpStatusCode.Forbidden)
            throw new InvalidOperationException("SubSource rejected the API key — check it in Settings");
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            return """{"data":[]}""";
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SubSource error {(int)response.StatusCode}");
        return content;
    }

    private HttpRequestMessage BuildRequest(string url, AppSettings settings)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("X-API-Key", settings.SubSource.ApiKey.Trim());
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);
        return request;
    }

    private static SubtitlePayload ExtractFromZip(byte[] zipBytes)
    {
        using var archive = new ZipArchive(new MemoryStream(zipBytes), ZipArchiveMode.Read);
        var entry = archive.Entries
            .Where(e => SubtitleNaming.SubtitleExtensions.Contains(
                Path.GetExtension(e.Name), StringComparer.OrdinalIgnoreCase))
            .OrderBy(e => Path.GetExtension(e.Name).Equals(".srt", StringComparison.OrdinalIgnoreCase) ? 0 : 1)
            .ThenByDescending(e => e.Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException("SubSource archive contained no subtitle file");

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new SubtitlePayload(memory.ToArray(), Path.GetExtension(entry.Name).ToLowerInvariant());
    }

    /// <summary>SubSource sends ids as either JSON numbers or strings.</summary>
    private sealed class FlexibleStringConverter : JsonConverter<string?>
    {
        public override string? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
            reader.TokenType switch
            {
                JsonTokenType.Number => reader.GetInt64().ToString(),
                JsonTokenType.String => reader.GetString(),
                _ => null,
            };

        public override void Write(Utf8JsonWriter writer, string? value, JsonSerializerOptions options) =>
            writer.WriteStringValue(value);
    }

    private sealed class TitleSearchResponse
    {
        public List<TitleDto>? Data { get; set; }
    }

    private sealed class TitleDto
    {
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string? MovieId { get; set; }
        public string? Title { get; set; }
        public string? AlternateTitle { get; set; }
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string? ReleaseYear { get; set; }
    }

    private sealed class ListingResponse
    {
        public List<SubtitleDto>? Data { get; set; }
    }

    private sealed class SubtitleDto
    {
        [JsonConverter(typeof(FlexibleStringConverter))]
        public string? SubtitleId { get; set; }
        public string? Language { get; set; }
        public List<string>? ReleaseInfo { get; set; }
        public bool? HearingImpaired { get; set; }
    }
}
