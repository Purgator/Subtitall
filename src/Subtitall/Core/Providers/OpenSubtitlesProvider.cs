using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Subtitall.Core.Providers;

/// <summary>
/// OpenSubtitles REST API (api.opensubtitles.com v1). Requires a per-app API key;
/// an optional user login raises the download quota (anonymous: 5/day/IP).
/// </summary>
public sealed class OpenSubtitlesProvider(IHttpClientFactory httpFactory, ILogger<OpenSubtitlesProvider> logger)
    : ISubtitleProvider
{
    public const string ProviderName = "OpenSubtitles";
    private const string BaseUrl = "https://api.opensubtitles.com/api/v1";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    // Global API limit is 5 req/s per IP — stay well under it during batch searches.
    private readonly RateGate _gate = new(TimeSpan.FromMilliseconds(350));
    private readonly SemaphoreSlim _loginLock = new(1, 1);
    private string? _token;
    private string _tokenKey = "";
    private DateTime _tokenIssuedUtc;

    public string Name => ProviderName;

    public bool IsConfigured(AppSettings settings) =>
        settings.OpenSubtitles.Enabled && settings.OpenSubtitles.ApiKey.Trim().Length > 0;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        MediaItem item, string language, AppSettings settings, CancellationToken ct)
    {
        // Params must be alphabetically sorted, lowercase, with '+' for spaces (API best practice
        // — unsorted params cause redirects that can drop the Api-Key header).
        var parsed = item.Parsed;
        var query = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["languages"] = language.ToLowerInvariant(),
            ["query"] = parsed.Title.ToLowerInvariant(),
        };

        if (MovieHash.Compute(item.Path) is { } hash)
            query["moviehash"] = hash;

        if (parsed.Season.HasValue && parsed.Episode.HasValue)
        {
            query["episode_number"] = parsed.Episode.Value.ToString();
            query["season_number"] = parsed.Season.Value.ToString();
            query["type"] = "episode";
        }
        else if (parsed.AbsoluteEpisode is int absolute)
        {
            // Anime absolute numbering — OpenSubtitles indexes many anime by episode
            // number without a season, so a bare episode_number narrows the search well.
            query["episode_number"] = absolute.ToString();
            query["type"] = "episode";
        }
        else if (!parsed.IsEpisode)
        {
            query["type"] = "movie";
            if (parsed.Year.HasValue)
                query["year"] = parsed.Year.Value.ToString();
        }

        var url = $"{BaseUrl}/subtitles?{BuildQueryString(query)}";
        var response = await SendAsync(HttpMethod.Get, url, settings, jsonBody: null, withToken: false, ct);
        var result = JsonSerializer.Deserialize<SearchResponse>(response, Json);

        var candidates = new List<SubtitleCandidate>();
        foreach (var entry in result?.Data ?? [])
        {
            var attributes = entry.Attributes;
            var fileId = attributes?.Files?.FirstOrDefault()?.FileId;
            if (attributes is null || fileId is null)
                continue;
            candidates.Add(new SubtitleCandidate
            {
                Provider = ProviderName,
                DownloadRef = fileId.Value.ToString(),
                Language = attributes.Language ?? language,
                ReleaseName = attributes.Release ?? attributes.Files?.FirstOrDefault()?.FileName ?? "",
                Downloads = attributes.DownloadCount ?? 0,
                HashMatch = attributes.MoviehashMatch ?? false,
                HearingImpaired = attributes.HearingImpaired ?? false,
            });
        }
        return candidates;
    }

    public async Task<SubtitlePayload> DownloadAsync(
        SubtitleCandidate candidate, AppSettings settings, CancellationToken ct)
    {
        var body = JsonSerializer.Serialize(new { file_id = int.Parse(candidate.DownloadRef) });

        string response;
        try
        {
            response = await SendAsync(HttpMethod.Post, $"{BaseUrl}/download", settings, body, withToken: true, ct);
        }
        catch (ApiException ex) when (ex.StatusCode == 500 && ex.Body.Contains("invalid", StringComparison.OrdinalIgnoreCase))
        {
            // Expired JWT surfaces as HTTP 500 "invalid" — re-login once and retry.
            InvalidateToken();
            response = await SendAsync(HttpMethod.Post, $"{BaseUrl}/download", settings, body, withToken: true, ct);
        }

        var download = JsonSerializer.Deserialize<DownloadResponse>(response, Json);
        if (download?.Link is null)
            throw new InvalidOperationException("OpenSubtitles returned no download link");
        if (download.Remaining is < 0)
            throw new InvalidOperationException($"Download quota exceeded — {download.Message ?? "resets at midnight UTC"}");

        var http = httpFactory.CreateClient();
        var bytes = await http.GetByteArrayAsync(download.Link, ct);
        return new SubtitlePayload(bytes, ".srt");
    }

    private async Task<string> SendAsync(
        HttpMethod method, string url, AppSettings settings, string? jsonBody, bool withToken,
        CancellationToken ct, bool allowRetry = true)
    {
        await _gate.WaitAsync(ct);

        using var request = new HttpRequestMessage(method, url);
        request.Headers.TryAddWithoutValidation("Api-Key", settings.OpenSubtitles.ApiKey.Trim());
        request.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);
        request.Headers.TryAddWithoutValidation("Accept", "*/*");
        if (withToken && await GetTokenAsync(settings, ct) is { } token)
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {token}");
        if (jsonBody is not null)
            request.Content = new StringContent(jsonBody, Encoding.UTF8, "application/json");

        var http = httpFactory.CreateClient();
        using var response = await http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode == System.Net.HttpStatusCode.TooManyRequests && allowRetry)
        {
            await Task.Delay(TimeSpan.FromSeconds(1.5), ct);
            return await SendAsync(method, url, settings, jsonBody, withToken, ct, allowRetry: false);
        }

        if (!response.IsSuccessStatusCode)
            throw new ApiException((int)response.StatusCode, content, BuildErrorMessage(response.StatusCode, content));
        return content;
    }

    private async Task<string?> GetTokenAsync(AppSettings settings, CancellationToken ct)
    {
        var config = settings.OpenSubtitles;
        if (config.Username.Trim().Length == 0 || config.Password.Length == 0)
            return null; // Anonymous mode — Api-Key only, 5 downloads/day/IP.

        var cacheKey = $"{config.Username}|{config.ApiKey}";
        if (_token is not null && _tokenKey == cacheKey && DateTime.UtcNow - _tokenIssuedUtc < TimeSpan.FromHours(20))
            return _token;

        await _loginLock.WaitAsync(ct);
        try
        {
            if (_token is not null && _tokenKey == cacheKey && DateTime.UtcNow - _tokenIssuedUtc < TimeSpan.FromHours(20))
                return _token;

            await _gate.WaitAsync(ct);
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/login");
            request.Headers.TryAddWithoutValidation("Api-Key", config.ApiKey.Trim());
            request.Headers.TryAddWithoutValidation("User-Agent", UserAgent.Value);
            request.Content = new StringContent(
                JsonSerializer.Serialize(new { username = config.Username.Trim(), password = config.Password }),
                Encoding.UTF8, "application/json");

            var http = httpFactory.CreateClient();
            using var response = await http.SendAsync(request, ct);
            var content = await response.Content.ReadAsStringAsync(ct);
            if (!response.IsSuccessStatusCode)
                throw new InvalidOperationException($"OpenSubtitles login failed ({(int)response.StatusCode}) — check username/password");

            var login = JsonSerializer.Deserialize<LoginResponse>(content, Json);
            if (login?.Token is null)
                throw new InvalidOperationException("OpenSubtitles login returned no token");

            logger.LogInformation("Logged in to OpenSubtitles as {User}", config.Username);
            _token = login.Token;
            _tokenKey = cacheKey;
            _tokenIssuedUtc = DateTime.UtcNow;
            return _token;
        }
        finally
        {
            _loginLock.Release();
        }
    }

    private void InvalidateToken()
    {
        _token = null;
        _tokenKey = "";
    }

    private static string BuildQueryString(SortedDictionary<string, string> parameters) =>
        string.Join("&", parameters.Select(p => $"{p.Key}={EscapePlus(p.Value)}"));

    private static string EscapePlus(string value) =>
        Uri.EscapeDataString(value).Replace("%20", "+");

    private static string BuildErrorMessage(System.Net.HttpStatusCode status, string body)
    {
        var detail = "";
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("message", out var message))
                detail = message.GetString() ?? "";
        }
        catch (JsonException) { }
        return $"OpenSubtitles error {(int)status}{(detail.Length > 0 ? $": {detail}" : "")}";
    }

    private sealed class ApiException(int statusCode, string body, string message) : Exception(message)
    {
        public int StatusCode { get; } = statusCode;
        public string Body { get; } = body;
    }

    private sealed class SearchResponse
    {
        public List<SearchEntry>? Data { get; set; }
    }

    private sealed class SearchEntry
    {
        public SubtitleAttributes? Attributes { get; set; }
    }

    private sealed class SubtitleAttributes
    {
        public string? Language { get; set; }
        public string? Release { get; set; }
        public int? DownloadCount { get; set; }
        public bool? MoviehashMatch { get; set; }
        public bool? HearingImpaired { get; set; }
        public List<SubtitleFileDto>? Files { get; set; }
    }

    private sealed class SubtitleFileDto
    {
        public int? FileId { get; set; }
        public string? FileName { get; set; }
    }

    private sealed class DownloadResponse
    {
        public string? Link { get; set; }
        public int? Remaining { get; set; }
        public string? Message { get; set; }
    }

    private sealed class LoginResponse
    {
        public string? Token { get; set; }

        [JsonPropertyName("base_url")]
        public string? BaseUrl { get; set; }
    }
}
