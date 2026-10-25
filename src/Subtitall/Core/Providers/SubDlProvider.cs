using System.IO.Compression;
using System.Text.Json;

namespace Subtitall.Core.Providers;

/// <summary>
/// SubDL API (api.subdl.com) — free API key, ~2000 searches/day.
/// Downloads are ZIPs (or raw files for season-pack "unpack" entries).
/// </summary>
public sealed class SubDlProvider(IHttpClientFactory httpFactory) : ISubtitleProvider
{
    public const string ProviderName = "SubDL";
    private const string ApiUrl = "https://api.subdl.com/api/v1/subtitles";
    private const string DownloadBase = "https://dl.subdl.com";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    private readonly RateGate _gate = new(TimeSpan.FromMilliseconds(400));

    public string Name => ProviderName;

    public bool IsConfigured(AppSettings settings) =>
        settings.SubDl.Enabled && settings.SubDl.ApiKey.Trim().Length > 0;

    public async Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        MediaItem item, string language, AppSettings settings, CancellationToken ct)
    {
        // file_name search matches the release best; fall back to a title search.
        var byFile = await QueryAsync(item, language, settings,
            ("file_name", Path.GetFileNameWithoutExtension(item.FileName)), ct);
        if (byFile.Count > 0)
            return byFile;
        return await QueryAsync(item, language, settings, ("film_name", item.Parsed.Title), ct);
    }

    private async Task<IReadOnlyList<SubtitleCandidate>> QueryAsync(
        MediaItem item, string language, AppSettings settings,
        (string Key, string Value) nameParam, CancellationToken ct)
    {
        var parsed = item.Parsed;
        var subdlLang = Languages.ToSubDl(language);
        var url = $"{ApiUrl}?api_key={Uri.EscapeDataString(settings.SubDl.ApiKey.Trim())}" +
                  $"&{nameParam.Key}={Uri.EscapeDataString(nameParam.Value)}" +
                  $"&languages={Uri.EscapeDataString(subdlLang)}&subs_per_page=30&unpack=1";

        if (parsed.Season.HasValue && parsed.Episode.HasValue)
            url += $"&type=tv&season_number={parsed.Season}&episode_number={parsed.Episode}";
        else if (!parsed.IsEpisode && parsed.Year.HasValue)
            url += $"&type=movie&year={parsed.Year}";

        await _gate.WaitAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        // api.subdl.com sits behind bot filtering that rejects bare HTTP-library user agents.
        request.Headers.TryAddWithoutValidation("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) Subtitall/{UserAgent.Version}");

        var http = httpFactory.CreateClient();
        using var response = await http.SendAsync(request, ct);
        var content = await response.Content.ReadAsStringAsync(ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SubDL error {(int)response.StatusCode} — check your API key");

        var result = JsonSerializer.Deserialize<SearchResponse>(content, Json);
        if (result?.Status != true || result.Subtitles is null)
            return [];

        var candidates = new List<SubtitleCandidate>();
        foreach (var sub in result.Subtitles)
        {
            if (sub.Url is null || !LanguageMatches(sub.Language, language))
                continue;

            if (parsed.Season.HasValue && parsed.Episode.HasValue)
            {
                if (sub.Season.HasValue && sub.Season != parsed.Season)
                    continue;

                if (IsFullSeason(sub))
                {
                    // Season pack: only usable when an unpack entry pinpoints our episode.
                    var match = sub.UnpackFiles?.FirstOrDefault(f =>
                        f.Url is not null && f.Season == parsed.Season && f.Episode == parsed.Episode);
                    if (match is null)
                        continue;
                    candidates.Add(Candidate(match.Url!, language, match.ReleaseName ?? match.Name ?? sub.ReleaseName ?? ""));
                    continue;
                }

                if (sub.Episode.HasValue && sub.Episode != parsed.Episode)
                    continue;
            }

            candidates.Add(Candidate(sub.Url, language, sub.ReleaseName ?? sub.Name ?? "", sub.Hi ?? false));
        }
        return candidates;
    }

    private static bool IsFullSeason(SubtitleDto sub) =>
        sub.FullSeason == true || (!sub.Episode.HasValue && sub.EpisodeEnd > sub.EpisodeFrom);

    private static bool LanguageMatches(string? subdlCode, string internalCode) =>
        subdlCode is not null && Languages.FromSubDl(subdlCode) == internalCode;

    private static SubtitleCandidate Candidate(
        string url, string language, string releaseName, bool hearingImpaired = false) => new()
    {
        Provider = ProviderName,
        DownloadRef = url,
        Language = language,
        ReleaseName = releaseName,
        HearingImpaired = hearingImpaired,
    };

    public async Task<SubtitlePayload> DownloadAsync(
        SubtitleCandidate candidate, AppSettings settings, CancellationToken ct)
    {
        var url = candidate.DownloadRef.StartsWith("http", StringComparison.OrdinalIgnoreCase)
            ? candidate.DownloadRef
            : DownloadBase + candidate.DownloadRef;

        await _gate.WaitAsync(ct);
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("User-Agent",
            $"Mozilla/5.0 (Windows NT 10.0; Win64; x64) Subtitall/{UserAgent.Version}");

        var http = httpFactory.CreateClient();
        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode)
            throw new InvalidOperationException($"SubDL download failed ({(int)response.StatusCode})");
        var bytes = await response.Content.ReadAsByteArrayAsync(ct);

        // Zip URLs return archives; unpack-file URLs return the raw subtitle. Sniff the magic bytes.
        if (bytes.Length > 3 && bytes[0] == 'P' && bytes[1] == 'K')
            return ExtractFromZip(bytes);

        var extension = Path.GetExtension(new Uri(url).AbsolutePath);
        if (!SubtitleNaming.SubtitleExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase))
            extension = ".srt";
        return new SubtitlePayload(bytes, extension);
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
            ?? throw new InvalidOperationException("SubDL archive contained no subtitle file");

        using var stream = entry.Open();
        using var memory = new MemoryStream();
        stream.CopyTo(memory);
        return new SubtitlePayload(memory.ToArray(), Path.GetExtension(entry.Name).ToLowerInvariant());
    }

    private sealed class SearchResponse
    {
        public bool? Status { get; set; }
        public List<SubtitleDto>? Subtitles { get; set; }
    }

    private sealed class SubtitleDto
    {
        public string? ReleaseName { get; set; }
        public string? Name { get; set; }
        public string? Url { get; set; }
        public string? Language { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public int? EpisodeFrom { get; set; }
        public int? EpisodeEnd { get; set; }
        public bool? FullSeason { get; set; }
        public bool? Hi { get; set; }
        public List<UnpackFileDto>? UnpackFiles { get; set; }
    }

    private sealed class UnpackFileDto
    {
        public string? Name { get; set; }
        public string? ReleaseName { get; set; }
        public int? Season { get; set; }
        public int? Episode { get; set; }
        public string? Url { get; set; }
    }
}
