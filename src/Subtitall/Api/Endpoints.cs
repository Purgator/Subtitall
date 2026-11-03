using ElectronNET.API;
using ElectronNET.API.Entities;
using Subtitall.Core;
using Subtitall.Core.Providers;

namespace Subtitall.Api;

public static class Endpoints
{
    public static void MapSubtitallApi(this WebApplication app)
    {
        var api = app.MapGroup("/api");

        // ---- Meta ----------------------------------------------------------
        api.MapGet("/meta", () => Results.Ok(new
        {
            version = UserAgent.Version,
            isElectron = ElectronBridge.IsActive,
            languages = Languages.All.Select(l => new { code = l.Code, name = l.Name }),
            // Which providers this build ships a key for (never the keys themselves).
            builtInKeys = new
            {
                openSubtitles = BuiltInKeys.OpenSubtitles.Length > 0,
                tmdb = BuiltInKeys.Tmdb.Length > 0,
            },
        }));

        // ---- Library -------------------------------------------------------
        api.MapGet("/library", (LibraryService library, SettingsService settings) =>
        {
            library.RefreshSubtitleStatus(settings.Current.Languages);
            return Results.Ok(library.GetAll().Select(ToDto));
        });

        api.MapPost("/library/add", (
            AddPathsRequest request, LibraryService library, SettingsService settings,
            MediaDetectionService detection, EventBus events) =>
        {
            var added = library.AddPaths(request.Paths ?? []);
            library.RefreshSubtitleStatus(settings.Current.Languages);
            events.Publish("library-changed");
            detection.Queue(added);
            return Results.Ok(new { added = added.Count, items = library.GetAll().Select(ToDto) });
        });

        api.MapPost("/library/browse", async (
            BrowseRequest request, LibraryService library, SettingsService settings,
            MediaDetectionService detection, EventBus events) =>
        {
            if (!ElectronBridge.IsActive)
                return Results.Problem("File dialogs require the desktop app", statusCode: 501);

            var window = Electron.WindowManager.BrowserWindows.FirstOrDefault();
            var options = request.Mode == "folder"
                ? new OpenDialogOptions
                {
                    Title = "Add a folder — videos are scanned recursively",
                    Properties = [OpenDialogProperty.openDirectory],
                }
                : new OpenDialogOptions
                {
                    Title = "Add video files",
                    Properties = [OpenDialogProperty.openFile, OpenDialogProperty.multiSelections],
                    Filters =
                    [
                        new FileFilter { Name = "Videos", Extensions = ["mkv", "mp4", "avi", "mov", "m4v", "wmv", "flv", "webm", "mpg", "mpeg", "m2ts", "ogm"] },
                        new FileFilter { Name = "All files", Extensions = ["*"] },
                    ],
                };

            var paths = await Electron.Dialog.ShowOpenDialogAsync(window, options);
            var addedItems = paths is { Length: > 0 } ? library.AddPaths(paths) : [];
            library.RefreshSubtitleStatus(settings.Current.Languages);
            if (addedItems.Count > 0)
            {
                events.Publish("library-changed");
                detection.Queue(addedItems);
            }
            return Results.Ok(new { added = addedItems.Count, items = library.GetAll().Select(ToDto) });
        });

        // ---- Detection (which show / which season a file is) -----------------
        api.MapPost("/library/detect", async (MediaDetectionService detection, CancellationToken ct) =>
        {
            await detection.RunAllAsync(ct);
            return Results.NoContent();
        });

        // The user picked the show for every file parsed to the given title.
        api.MapPost("/library/show", async (
            ChooseShowRequest request, LibraryService library, ShowDetectionService shows,
            MediaDetectionService detection, CancellationToken ct) =>
        {
            if (string.IsNullOrWhiteSpace(request.Title) || request.Show is null)
                return Results.BadRequest(new { message = "A title and a show are required" });
            shows.Choose(request.Title, request.Show);
            var affected = library.GetAll()
                .Where(i => string.Equals(i.Parsed.Title.Trim(), request.Title.Trim(), StringComparison.OrdinalIgnoreCase))
                .ToList();
            await detection.RunAsync(affected, ct);
            return Results.Ok(affected.Select(ToDto));
        });

        // The user set the season for files whose names carry only an episode number.
        api.MapPost("/library/season", async (
            SetSeasonRequest request, LibraryService library, MediaDetectionService detection, CancellationToken ct) =>
        {
            var items = (request.ItemIds ?? []).Select(library.Get).OfType<MediaItem>().ToList();
            foreach (var item in items)
                item.SeasonOverride = request.Season is > 0 ? request.Season : null;
            await detection.RunAsync(items, ct);
            return Results.Ok(items.Select(ToDto));
        });

        api.MapDelete("/library/{id}", (string id, LibraryService library, EventBus events) =>
        {
            if (!library.Remove(id))
                return Results.NotFound();
            events.Publish("library-changed");
            return Results.NoContent();
        });

        api.MapDelete("/library", (LibraryService library, EventBus events) =>
        {
            library.Clear();
            events.Publish("library-changed");
            return Results.NoContent();
        });

        api.MapPost("/shell/reveal", (RevealRequest request, LibraryService library) =>
        {
            var item = library.Get(request.ItemId);
            if (item is null || !File.Exists(item.Path))
                return Results.NotFound();
            if (ElectronBridge.IsActive)
                Electron.Shell.ShowItemInFolderAsync(item.Path);
            else if (OperatingSystem.IsWindows())
                System.Diagnostics.Process.Start("explorer.exe", $"/select,\"{item.Path}\"");
            return Results.NoContent();
        });

        // ---- Subtitles -----------------------------------------------------
        api.MapPost("/subtitles/search", (SearchRequest request, BatchSearchService batch) =>
            batch.Start(request.ItemIds)
                ? Results.Accepted()
                : Results.Conflict(new { message = "A search is already running" }));

        api.MapPost("/subtitles/cancel", (BatchSearchService batch) =>
        {
            batch.Cancel();
            return Results.NoContent();
        });

        // Lets the UI resync after an SSE reconnect (e.g. missed batch-finished event).
        api.MapGet("/subtitles/status", (BatchSearchService batch) =>
            Results.Ok(new { running = batch.IsRunning }));

        api.MapGet("/subtitles/candidates", async (
            string itemId, string language, LibraryService library, SubtitleSearchService search,
            MediaDetectionService detection, CancellationToken ct) =>
        {
            var item = library.Get(itemId);
            if (item is null)
                return Results.NotFound();
            await detection.EnsureAsync([item], ct);
            var report = await search.SearchAsync(item, language, ct);
            return Results.Ok(new { report.Candidates, report.Sources });
        });

        api.MapPost("/subtitles/download", async (
            DownloadRequest request, LibraryService library, SubtitleSearchService search,
            SettingsService settings, EventBus events, CancellationToken ct) =>
        {
            var item = library.Get(request.ItemId);
            if (item is null)
                return Results.NotFound();
            var outcome = await search.DownloadAsync(item, request.Candidate, ct);
            library.RefreshSubtitleStatus(settings.Current.Languages);
            events.Publish("library-changed");
            return Results.Ok(outcome);
        });

        // ---- Providers (status, quotas, login) --------------------------------
        api.MapGet("/providers/status", (
            IEnumerable<ISubtitleProvider> providers, SettingsService settings, ProviderHealth health) =>
        {
            var config = settings.Current;
            var now = DateTimeOffset.UtcNow;
            return Results.Ok(providers.Select(p => new
            {
                name = p.Name,
                configured = p.IsConfigured(config),
                cooldownUntil = health.CooldownUntil(p.Name),
                quota = health.Quota(p.Name) is { } q
                    ? new { q.Remaining, q.ResetAt, exhausted = q.Exhausted(now) }
                    : null,
                // OpenSubtitles only: whether downloads go through an account, and if the login worked.
                account = p is OpenSubtitlesProvider os
                    ? new
                    {
                        hasCredentials = OpenSubtitlesProvider.HasCredentials(config),
                        login = os.LastLogin,
                    }
                    : null,
            }));
        });

        api.MapPost("/providers/opensubtitles/login", async (
            IEnumerable<ISubtitleProvider> providers, SettingsService settings, CancellationToken ct) =>
        {
            var provider = providers.OfType<OpenSubtitlesProvider>().FirstOrDefault();
            if (provider is null)
                return Results.NotFound();
            return Results.Ok(await provider.TestLoginAsync(settings.Current, ct));
        });

        // ---- Updates ---------------------------------------------------------
        api.MapGet("/update/status", (UpdateService updates) => Results.Ok(updates.Status));

        api.MapPost("/update/check", async (UpdateService updates) =>
        {
            await updates.CheckAsync();
            return Results.Ok(updates.Status);
        });

        api.MapPost("/update/install", (UpdateService updates) =>
            updates.Install()
                ? Results.NoContent()
                : Results.Conflict(new { message = "No downloaded update to install" }));

        // ---- Settings ------------------------------------------------------
        api.MapGet("/settings", (SettingsService settings) => Results.Ok(settings.Current));

        api.MapPut("/settings", (AppSettings updated, SettingsService settings, LibraryService library, EventBus events) =>
        {
            settings.Save(updated);
            library.RefreshSubtitleStatus(settings.Current.Languages);
            events.Publish("settings-changed");
            events.Publish("library-changed");
            return Results.Ok(settings.Current);
        });

        // ---- Rename --------------------------------------------------------
        api.MapGet("/rename/shows", async (string q, ShowMetadataService metadata, CancellationToken ct) =>
            Results.Ok(await metadata.SearchShowsAsync(q, ct)));

        // Proposals come from each file's detected show — the same detection the subtitle
        // search uses — so the Rename tab works on the same list as the Files tab.
        api.MapPost("/rename/proposals", async (
            ProposalsRequest request, LibraryService library, MediaDetectionService detection,
            RenameService rename, SettingsService settings, CancellationToken ct) =>
        {
            var items = request.ItemIds is { Count: > 0 }
                ? request.ItemIds.Select(library.Get).OfType<MediaItem>().ToList()
                : library.GetAll();
            await detection.EnsureAsync(items, ct);

            var proposals = new List<RenameProposal>();
            foreach (var group in items.GroupBy(i => i.Detection?.Show is { } s ? ShowDetectionService.Key(s.Show) : ""))
            {
                var show = group.First().Detection?.Show;
                if (show is null)
                {
                    proposals.AddRange(group.Select(i => new RenameProposal(i.Id, i.FileName, i.FileName,
                        !i.Parsed.IsEpisode ? "Not a TV episode"
                        : i.Detection is null ? "Show not detected yet"
                        : i.Detection.Status == DetectionStatus.Ambiguous ? "Several shows match — pick one"
                        : "Show not found — pick it")));
                    continue;
                }
                proposals.AddRange(rename.BuildProposals(
                    group.ToList(), show.Show.Name, show.Episodes, settings.Current.RenameTemplate, show.Aliases));
            }
            var order = items.Select((i, index) => (i.Id, index)).ToDictionary(p => p.Id, p => p.index);
            return Results.Ok(proposals.OrderBy(p => order[p.ItemId]).ToList());
        });

        api.MapPost("/rename/apply", (
            ApplyRenameRequest request, RenameService rename, LibraryService library,
            SettingsService settings, MediaDetectionService detection, EventBus events) =>
        {
            var outcomes = rename.Apply(request.Proposals ?? []);
            library.RefreshSubtitleStatus(settings.Current.Languages);
            events.Publish("library-changed");
            // Renamed files parse differently now (explicit season/episode) — refresh their guesses.
            detection.Queue(outcomes.Where(o => o.Renamed).Select(o => library.Get(o.ItemId)).OfType<MediaItem>());
            return Results.Ok(outcomes);
        });

        // ---- Events (SSE) ----------------------------------------------------
        api.MapGet("/events", async (HttpContext context, EventBus events) =>
        {
            context.Response.Headers.ContentType = "text/event-stream";
            context.Response.Headers.CacheControl = "no-cache";
            var (id, reader) = events.Subscribe();
            try
            {
                await context.Response.WriteAsync("retry: 2000\n\n", context.RequestAborted);
                await context.Response.Body.FlushAsync(context.RequestAborted);

                // Keep a single WaitToReadAsync alive across heartbeats — spawning a new
                // one per iteration would pile up abandoned waiters on the channel.
                var readTask = reader.WaitToReadAsync(context.RequestAborted).AsTask();
                while (!context.RequestAborted.IsCancellationRequested)
                {
                    var completed = await Task.WhenAny(readTask, Task.Delay(15000, context.RequestAborted));
                    if (completed == readTask)
                    {
                        if (!await readTask)
                            break;
                        while (reader.TryRead(out var message))
                            await context.Response.WriteAsync($"data: {message}\n\n", context.RequestAborted);
                        readTask = reader.WaitToReadAsync(context.RequestAborted).AsTask();
                    }
                    else
                    {
                        await context.Response.WriteAsync(": ping\n\n", context.RequestAborted);
                    }
                    await context.Response.Body.FlushAsync(context.RequestAborted);
                }
            }
            catch (OperationCanceledException)
            {
                // Client disconnected.
            }
            finally
            {
                events.Unsubscribe(id);
            }
        });
    }

    private static object ToDto(MediaItem item) => new
    {
        id = item.Id,
        fileName = item.FileName,
        path = item.Path,
        sizeBytes = item.SizeBytes,
        parsed = new
        {
            title = item.Parsed.Title,
            season = item.Parsed.Season,
            episode = item.Parsed.Episode,
            endEpisode = item.Parsed.EndEpisode,
            absoluteEpisode = item.Parsed.AbsoluteEpisode,
            year = item.Parsed.Year,
            releaseGroup = item.Parsed.ReleaseGroup,
            seasonHint = item.Parsed.SeasonHint,
            finalSeasonHint = item.Parsed.FinalSeasonHint,
            isEpisode = item.Parsed.IsEpisode,
        },
        // Season/episode as the search and rename features will use them.
        effective = new { season = item.Effective.Season, episode = item.Effective.Episode, endEpisode = item.Effective.EndEpisode },
        guess = item.Guess is { } g ? new { g.Season, g.Episode, confidence = g.Confidence.ToString().ToLowerInvariant(), g.Reason } : null,
        seasonOverride = item.SeasonOverride,
        detection = item.Detection is { } d ? new
        {
            d.Title,
            status = d.Status switch
            {
                DetectionStatus.Resolved => "resolved",
                DetectionStatus.Ambiguous => "ambiguous",
                _ => "notFound",
            },
            d.ChosenByUser,
            show = d.Show is { } s ? ShowDto(s.Show) : null,
            seasons = d.Show?.Seasons ?? [],
            candidates = d.Candidates.Select(ShowDto),
        } : null,
        subtitles = item.Subtitles,
    };

    private static object ShowDto(TvShow show) => new
    {
        show.Id, show.Name, show.Premiered, show.Network, show.ImageUrl, show.Source,
    };

    public sealed record AddPathsRequest(List<string>? Paths);
    public sealed record BrowseRequest(string Mode);
    public sealed record RevealRequest(string ItemId);
    public sealed record SearchRequest(List<string>? ItemIds);
    public sealed record DownloadRequest(string ItemId, SubtitleCandidate Candidate);
    public sealed record ProposalsRequest(List<string>? ItemIds);
    public sealed record ChooseShowRequest(string Title, TvShow? Show);
    public sealed record SetSeasonRequest(List<string>? ItemIds, int? Season);
    public sealed record ApplyRenameRequest(List<RenameProposal>? Proposals);
}

/// <summary>Guards Electron API calls so the app also runs as a plain web app during development.</summary>
public static class ElectronBridge
{
    public static bool IsActive
    {
        get
        {
            try { return HybridSupport.IsElectronActive; }
            catch (Exception) { return false; }
        }
    }
}
