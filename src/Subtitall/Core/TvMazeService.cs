using System.Text.Json;
using System.Text.Json.Serialization;

namespace Subtitall.Core;

public sealed record TvShow(int Id, string Name, string? Premiered, string? Network, string? ImageUrl);

public sealed record TvEpisode(int Season, int Number, string Name, string? Airdate);

/// <summary>TVMaze API client (free, no key required) used for renaming and alias resolution.</summary>
public sealed class TvMazeService(IHttpClientFactory httpFactory)
{
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<IReadOnlyList<TvShow>> SearchShowsAsync(string query, CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient();
        var url = $"https://api.tvmaze.com/search/shows?q={Uri.EscapeDataString(query)}";
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var results = await response.Content.ReadFromJsonAsync<List<SearchResult>>(Json, ct) ?? [];
        return results
            .Where(r => r.Show is not null)
            .Select(r => new TvShow(
                r.Show!.Id,
                r.Show.Name ?? "?",
                r.Show.Premiered,
                r.Show.Network?.Name ?? r.Show.WebChannel?.Name,
                r.Show.Image?.Medium))
            .ToList();
    }

    public async Task<IReadOnlyList<TvEpisode>> GetEpisodesAsync(int showId, CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient();
        var url = $"https://api.tvmaze.com/shows/{showId}/episodes";
        using var response = await http.GetAsync(url, ct);
        response.EnsureSuccessStatusCode();
        var episodes = await response.Content.ReadFromJsonAsync<List<EpisodeDto>>(Json, ct) ?? [];
        return episodes
            .Where(e => e.Season.HasValue && e.Number.HasValue)
            .Select(e => new TvEpisode(e.Season!.Value, e.Number!.Value, e.Name ?? "", e.Airdate))
            .ToList();
    }

    /// <summary>Alternate titles ("akas") of a show — e.g. "Shingeki no Kyojin" for Attack on Titan.</summary>
    public async Task<IReadOnlyList<string>> GetAliasesAsync(int showId, CancellationToken ct = default)
    {
        var http = httpFactory.CreateClient();
        var url = $"https://api.tvmaze.com/shows/{showId}/akas";
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
            return [];
        var akas = await response.Content.ReadFromJsonAsync<List<AkaDto>>(Json, ct) ?? [];
        return akas
            .Select(a => a.Name?.Trim() ?? "")
            .Where(n => n.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    private sealed class SearchResult
    {
        public ShowDto? Show { get; set; }
    }

    private sealed class AkaDto
    {
        public string? Name { get; set; }
    }

    private sealed class ShowDto
    {
        public int Id { get; set; }
        public string? Name { get; set; }
        public string? Premiered { get; set; }
        public NamedDto? Network { get; set; }
        [JsonPropertyName("webChannel")]
        public NamedDto? WebChannel { get; set; }
        public ImageDto? Image { get; set; }
    }

    private sealed class EpisodeDto
    {
        public int? Season { get; set; }
        public int? Number { get; set; }
        public string? Name { get; set; }
        public string? Airdate { get; set; }
    }

    private sealed class NamedDto
    {
        public string? Name { get; set; }
    }

    private sealed class ImageDto
    {
        public string? Medium { get; set; }
    }
}
