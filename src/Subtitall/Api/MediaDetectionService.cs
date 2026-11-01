using Subtitall.Core;

namespace Subtitall.Api;

/// <summary>
/// Runs show detection and season guessing over the library, in the background after files are
/// added and on demand before a search or a rename. Files are handled per show (grouped by the
/// title parsed from their names) so that files which spell out their season can vouch for the
/// ones that don't. Every pass publishes a library-changed event so the UI picks up the result.
/// </summary>
public sealed class MediaDetectionService(
    LibraryService library,
    ShowDetectionService shows,
    EventBus events,
    ILogger<MediaDetectionService> logger)
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Detects in the background; errors are logged, never thrown.</summary>
    public void Queue(IEnumerable<MediaItem> items)
    {
        var snapshot = items.ToList();
        if (snapshot.Count == 0)
            return;
        _ = Task.Run(async () =>
        {
            try
            {
                await RunAsync(snapshot, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Background detection failed");
            }
        });
    }

    /// <summary>Makes sure every given item has been through detection (runs it if not).</summary>
    public async Task EnsureAsync(IReadOnlyList<MediaItem> items, CancellationToken ct)
    {
        var pending = items.Where(i => i.Parsed.IsEpisode && i.Detection is null).ToList();
        if (pending.Count > 0)
            await RunAsync(pending, ct);
    }

    /// <summary>Re-runs detection for the whole library (e.g. after a source came back online).</summary>
    public Task RunAllAsync(CancellationToken ct) => RunAsync(library.GetAll(), ct);

    /// <summary>
    /// Detects the shows of the given items. Each item's whole title group (every library item
    /// parsed to the same title) is processed together, since siblings inform the season guess.
    /// </summary>
    public async Task RunAsync(IReadOnlyList<MediaItem> items, CancellationToken ct)
    {
        var titles = items
            .Where(i => i.Parsed.IsEpisode)
            .Select(i => i.Parsed.Title.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (titles.Count == 0)
            return;

        await _gate.WaitAsync(ct);
        try
        {
            var all = library.GetAll();
            foreach (var title in titles)
            {
                ct.ThrowIfCancellationRequested();
                var group = all
                    .Where(i => i.Parsed.IsEpisode && string.Equals(i.Parsed.Title.Trim(), title, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (group.Count == 0)
                    continue;
                await DetectGroupAsync(title, group, ct);
                events.Publish("library-changed");
            }
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task DetectGroupAsync(string title, List<MediaItem> group, CancellationToken ct)
    {
        ShowDetection detection;
        try
        {
            var year = group.Select(i => i.Parsed.Year).FirstOrDefault(y => y.HasValue);
            detection = await shows.DetectAsync(title, year, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Show detection failed for {Title}", title);
            detection = new ShowDetection(title, DetectionStatus.NotFound, null, []);
        }

        var guesses = EpisodeGuesser.Guess(
            group.Select(i => new GuessInput(i.Parsed, i.SeasonOverride)).ToList(),
            detection.Show?.Episodes);
        for (var i = 0; i < group.Count; i++)
        {
            group[i].Detection = detection;
            group[i].Guess = guesses[i];
        }
    }
}
