using System.Collections.Concurrent;

namespace Subtitall.Core;

/// <summary>
/// Resolves a filename title to the show's other known names via the configured metadata
/// sources ("Shingeki no Kyojin" → ["Attack on Titan", "L'attaque des Titans", …]) so
/// subtitle providers that only know one of them still find the show. Cached per title.
/// </summary>
public sealed class ShowAliasResolver(ShowMetadataService metadata, ILogger<ShowAliasResolver> logger)
{
    private readonly ConcurrentDictionary<string, IReadOnlyList<string>> _cache =
        new(StringComparer.OrdinalIgnoreCase);

    public async Task<IReadOnlyList<string>> GetAlternateNamesAsync(string title, CancellationToken ct)
    {
        if (_cache.TryGetValue(title, out var cached))
            return cached;

        IReadOnlyList<string> names = [];
        try
        {
            // TVMaze search matches alternate titles too, so a romaji query finds the show.
            var show = (await metadata.SearchShowsAsync(title, ct)).FirstOrDefault();
            if (show is not null)
            {
                var aliases = await metadata.GetAliasesAsync(show.Source, show.Id, show.Name, ct);
                names = new[] { show.Name }.Concat(aliases)
                    .Where(n => !string.Equals(n, title, StringComparison.OrdinalIgnoreCase))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }
            _cache[title] = names; // Only cache successful lookups — outages shouldn't stick.
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Alias lookup failed for {Title}", title);
        }
        return names;
    }
}
