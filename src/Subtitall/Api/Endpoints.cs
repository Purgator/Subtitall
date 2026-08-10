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
        }));

        // ---- Library -------------------------------------------------------
        api.MapGet("/library", (LibraryService library, SettingsService settings) =>
        {
            library.RefreshSubtitleStatus(settings.Current.Languages);
            return Results.Ok(library.GetAll().Select(ToDto));
        });

        api.MapPost("/library/add", (AddPathsRequest request, LibraryService library, SettingsService settings, EventBus events) =>
        {
            var added = library.AddPaths(request.Paths ?? []);
            library.RefreshSubtitleStatus(settings.Current.Languages);
            events.Publish("library-changed");
            return Results.Ok(new { added = added.Count, items = library.GetAll().Select(ToDto) });
        });

        api.MapPost("/library/browse", async (BrowseRequest request, LibraryService library, SettingsService settings, EventBus events) =>
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
            var added = paths is { Length: > 0 } ? library.AddPaths(paths).Count : 0;
            library.RefreshSubtitleStatus(settings.Current.Languages);
            if (added > 0)
                events.Publish("library-changed");
            return Results.Ok(new { added, items = library.GetAll().Select(ToDto) });
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
            string itemId, string language, LibraryService library, SubtitleSearchService search, CancellationToken ct) =>
        {
            var item = library.Get(itemId);
            if (item is null)
                return Results.NotFound();
            var candidates = await search.SearchAsync(item, language, ct);
            return Results.Ok(candidates);
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
        api.MapGet("/rename/shows", async (string q, TvMazeService tvmaze, CancellationToken ct) =>
            Results.Ok(await tvmaze.SearchShowsAsync(q, ct)));

        api.MapPost("/rename/proposals", async (
            ProposalsRequest request, LibraryService library, TvMazeService tvmaze,
            RenameService rename, SettingsService settings, CancellationToken ct) =>
        {
            var episodes = await tvmaze.GetEpisodesAsync(request.ShowId, ct);
            var items = request.ItemIds is { Count: > 0 }
                ? request.ItemIds.Select(library.Get).OfType<MediaItem>().ToList()
                : library.GetAll();
            var proposals = rename.BuildProposals(items, request.ShowName, episodes, settings.Current.RenameTemplate);
            return Results.Ok(proposals);
        });

        api.MapPost("/rename/apply", (
            ApplyRenameRequest request, RenameService rename, LibraryService library,
            SettingsService settings, EventBus events) =>
        {
            var outcomes = rename.Apply(request.Proposals ?? []);
            library.RefreshSubtitleStatus(settings.Current.Languages);
            events.Publish("library-changed");
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
            isEpisode = item.Parsed.IsEpisode,
        },
        subtitles = item.Subtitles,
    };

    public sealed record AddPathsRequest(List<string>? Paths);
    public sealed record BrowseRequest(string Mode);
    public sealed record RevealRequest(string ItemId);
    public sealed record SearchRequest(List<string>? ItemIds);
    public sealed record DownloadRequest(string ItemId, SubtitleCandidate Candidate);
    public sealed record ProposalsRequest(int ShowId, string ShowName, List<string>? ItemIds);
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
