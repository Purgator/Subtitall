using System.Text.Json;

namespace Subtitall.Core;

/// <summary>
/// TMDB API v3 client (api.themoviedb.org) — optional second metadata source for renaming.
/// Accepts either a v3 API key or a v4 read-access token (JWTs start with "eyJ").
/// </summary>
public sealed class TmdbService(IHttpClientFactory httpFactory, SettingsService settings)
{
    public const string SourceName = "TMDB";
    private const string BaseUrl = "https://api.themoviedb.org/3";
    private const string ImageBase = "https://image.tmdb.org/t/p/w154";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        PropertyNameCaseInsensitive = true,
    };

    public bool IsConfigured => settings.Current.Tmdb.Enabled && ApiKey.Length > 0;

    private string ApiKey => settings.Current.Tmdb.ApiKey.Trim();

    public async Task<IReadOnlyList<TvShow>> SearchShowsAsync(string query, CancellationToken ct = default)
    {
        var result = await GetAsync<SearchResponse>(
            $"/search/tv?query={Uri.EscapeDataString(query)}&include_adult=false", ct);
        return (result?.Results ?? [])
            .Select(s => new TvShow(
                s.Id,
                s.Name ?? "?",
                s.FirstAirDate,
                Network: null,
                s.PosterPath is { Length: > 0 } poster ? ImageBase + poster : null,
                SourceName))
            .ToList();
    }

    /// <summary>All episodes of a show — TMDB serves them one season at a time.</summary>
    public async Task<IReadOnlyList<TvEpisode>> GetEpisodesAsync(int showId, CancellationToken ct = default)
    {
        var show = await GetAsync<ShowDto>($"/tv/{showId}", ct);
        var episodes = new List<TvEpisode>();
        for (var season = 1; season <= (show?.NumberOfSeasons ?? 0); season++)
        {
            var dto = await GetAsync<SeasonDto>($"/tv/{showId}/season/{season}", ct);
            episodes.AddRange((dto?.Episodes ?? [])
                .Where(e => e.SeasonNumber.HasValue && e.EpisodeNumber.HasValue)
                .Select(e => new TvEpisode(e.SeasonNumber!.Value, e.EpisodeNumber!.Value, e.Name ?? "", e.AirDate)));
        }
        return episodes;
    }

    public async Task<IReadOnlyList<string>> GetAliasesAsync(int showId, CancellationToken ct = default)
    {
        var result = await GetAsync<AlternativeTitlesDto>($"/tv/{showId}/alternative_titles", ct);
        return (result?.Results ?? [])
            .Select(t => t.Title?.Trim() ?? "")
            .Where(t => t.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private async Task<T?> GetAsync<T>(string pathAndQuery, CancellationToken ct)
    {
        var url = BaseUrl + pathAndQuery;
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.TryAddWithoutValidation("Accept", "application/json");
        if (ApiKey.StartsWith("eyJ", StringComparison.Ordinal))
            request.Headers.TryAddWithoutValidation("Authorization", $"Bearer {ApiKey}");
        else
            url += (pathAndQuery.Contains('?') ? "&" : "?") + $"api_key={Uri.EscapeDataString(ApiKey)}";
        // The URL may have gained the api_key after the request was created.
        request.RequestUri = new Uri(url);

        var http = httpFactory.CreateClient();
        using var response = await http.SendAsync(request, ct);
        if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            throw new InvalidOperationException("TMDB rejected the API key — check it in Settings");
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<T>(Json, ct);
    }

    private sealed class SearchResponse
    {
        public List<ShowDto>? Results { get; set; }
    }

    private sealed class ShowDto
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? FirstAirDate { get; set; }
        public string? PosterPath { get; set; }
        public int? NumberOfSeasons { get; set; }
    }

    private sealed class SeasonDto
    {
        public List<EpisodeDto>? Episodes { get; set; }
    }

    private sealed class EpisodeDto
    {
        public int? SeasonNumber { get; set; }
        public int? EpisodeNumber { get; set; }
        public string? Name { get; set; }
        public string? AirDate { get; set; }
    }

    private sealed class AlternativeTitlesDto
    {
        public List<TitleDto>? Results { get; set; }
    }

    private sealed class TitleDto
    {
        public string? Title { get; set; }
    }
}
