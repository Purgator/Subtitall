namespace Subtitall.Core;

/// <summary>
/// Fans show lookups out to every configured metadata source (TVMaze keyless, TMDB with a key)
/// and merges the results, so renaming keeps working when one source is down or missing a show.
/// </summary>
public sealed class ShowMetadataService(
    TvMazeService tvmaze,
    TmdbService tmdb,
    ILogger<ShowMetadataService> logger)
{
    /// <summary>Searches all configured sources in parallel; TVMaze results lead, TMDB fills gaps.</summary>
    public async Task<IReadOnlyList<TvShow>> SearchShowsAsync(string query, CancellationToken ct = default)
    {
        var tvmazeTask = Guarded(() => tvmaze.SearchShowsAsync(query, ct), TvMazeService.SourceName);
        var tmdbTask = tmdb.IsConfigured
            ? Guarded(() => tmdb.SearchShowsAsync(query, ct), TmdbService.SourceName)
            : Task.FromResult<IReadOnlyList<TvShow>>([]);
        await Task.WhenAll(tvmazeTask, tmdbTask);
        return MergeShows(tvmazeTask.Result, tmdbTask.Result);
    }

    /// <summary>
    /// Episodes from the source the user picked the show on; when that source has none
    /// (rare, but TVMaze misses some web-only shows' specials and vice versa), the other
    /// source is searched by name and used instead.
    /// </summary>
    public async Task<IReadOnlyList<TvEpisode>> GetEpisodesAsync(
        string source, int showId, string showName, CancellationToken ct = default)
    {
        var episodes = await Guarded(
            () => source == TmdbService.SourceName ? tmdb.GetEpisodesAsync(showId, ct) : tvmaze.GetEpisodesAsync(showId, ct),
            source);
        if (episodes.Count > 0)
            return episodes;

        var fallback = await FindOnOtherSourceAsync(source, showName, ct);
        if (fallback is null)
            return episodes;
        logger.LogInformation("{Source} had no episodes for {Show} — using {Other}", source, showName, fallback.Source);
        return await Guarded(
            () => fallback.Source == TmdbService.SourceName
                ? tmdb.GetEpisodesAsync(fallback.Id, ct)
                : tvmaze.GetEpisodesAsync(fallback.Id, ct),
            fallback.Source);
    }

    /// <summary>Alternate titles from the chosen source, topped up by the other one when configured.</summary>
    public async Task<IReadOnlyList<string>> GetAliasesAsync(
        string source, int showId, string showName, CancellationToken ct = default)
    {
        var aliases = (await Guarded(
            () => source == TmdbService.SourceName ? tmdb.GetAliasesAsync(showId, ct) : tvmaze.GetAliasesAsync(showId, ct),
            source)).ToList();

        var other = await FindOnOtherSourceAsync(source, showName, ct);
        if (other is not null)
        {
            var more = await Guarded(
                () => other.Source == TmdbService.SourceName
                    ? tmdb.GetAliasesAsync(other.Id, ct)
                    : tvmaze.GetAliasesAsync(other.Id, ct),
                other.Source);
            aliases.Add(other.Name);
            aliases.AddRange(more);
        }
        return aliases.Where(a => a.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }

    private async Task<TvShow?> FindOnOtherSourceAsync(string source, string showName, CancellationToken ct)
    {
        if (source == TmdbService.SourceName)
            return (await Guarded(() => tvmaze.SearchShowsAsync(showName, ct), TvMazeService.SourceName))
                .FirstOrDefault(s => TextMatch.MatchesShowName(s.Name, [showName]) || sameName(s));
        if (!tmdb.IsConfigured)
            return null;
        return (await Guarded(() => tmdb.SearchShowsAsync(showName, ct), TmdbService.SourceName))
            .FirstOrDefault(sameName);

        bool sameName(TvShow s) => string.Equals(s.Name, showName, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Primary results first; secondary entries are added unless the same show is already there.</summary>
    internal static IReadOnlyList<TvShow> MergeShows(
        IReadOnlyList<TvShow> primary, IReadOnlyList<TvShow> secondary)
    {
        static string Key(TvShow show) =>
            $"{show.Name.Trim().ToLowerInvariant()}|{show.Premiered?.Split('-').FirstOrDefault()}";

        var merged = new List<TvShow>(primary);
        var seen = primary.Select(Key).ToHashSet();
        merged.AddRange(secondary.Where(s => seen.Add(Key(s))));
        return merged;
    }

    private async Task<IReadOnlyList<T>> Guarded<T>(
        Func<Task<IReadOnlyList<T>>> call, string source)
    {
        try
        {
            return await call();
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "{Source} lookup failed", source);
            return [];
        }
    }
}
