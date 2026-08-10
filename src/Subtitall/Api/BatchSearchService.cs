using Subtitall.Core;

namespace Subtitall.Api;

/// <summary>Runs one batch subtitle search at a time, publishing progress over the event bus.</summary>
public sealed class BatchSearchService(
    LibraryService library,
    SettingsService settings,
    SubtitleSearchService search,
    EventBus events,
    ILogger<BatchSearchService> logger)
{
    private readonly Lock _lock = new();
    private CancellationTokenSource? _cts;
    private Task? _running;

    public bool IsRunning
    {
        get { lock (_lock) return _running is { IsCompleted: false }; }
    }

    /// <summary>Starts a batch for the given items (or the whole library). False if one is already running.</summary>
    public bool Start(IReadOnlyList<string>? itemIds)
    {
        lock (_lock)
        {
            if (_running is { IsCompleted: false })
                return false;

            var items = itemIds is null or []
                ? library.GetAll()
                : itemIds.Select(library.Get).OfType<MediaItem>().ToList();

            _cts = new CancellationTokenSource();
            var token = _cts.Token;
            _running = Task.Run(() => RunAsync(items, token), CancellationToken.None);
            return true;
        }
    }

    public void Cancel()
    {
        lock (_lock) _cts?.Cancel();
    }

    private async Task RunAsync(IReadOnlyList<MediaItem> items, CancellationToken ct)
    {
        var languages = settings.Current.Languages;
        var total = items.Count * languages.Count;
        int done = 0, downloaded = 0, skipped = 0, failed = 0;
        var cancelled = false;

        events.Publish("batch-started", new { total });
        try
        {
            foreach (var item in items)
            {
                foreach (var language in languages)
                {
                    ct.ThrowIfCancellationRequested();
                    events.Publish("item-searching", new { itemId = item.Id, language, done, total });

                    SearchOutcome outcome;
                    try
                    {
                        outcome = await search.DownloadBestAsync(item, language, ct);
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Unexpected search failure for {File} [{Lang}]", item.FileName, language);
                        outcome = new SearchOutcome(item.Id, language, false, false, null, null, ex.Message);
                    }

                    done++;
                    if (outcome.Downloaded) downloaded++;
                    else if (outcome.Skipped) skipped++;
                    else failed++;

                    item.Subtitles[language] = outcome.Downloaded ||
                        (outcome.Skipped && SubtitleNaming.HasSubtitle(item.Path, language));
                    events.Publish("item-result", new
                    {
                        itemId = item.Id,
                        language,
                        outcome.Downloaded,
                        outcome.Skipped,
                        outcome.Provider,
                        outcome.Error,
                        done,
                        total,
                    });
                }
            }
        }
        catch (OperationCanceledException)
        {
            cancelled = true;
        }
        finally
        {
            library.RefreshSubtitleStatus(languages);
            events.Publish("batch-finished", new { downloaded, skipped, failed, cancelled, done, total });
        }
    }
}
