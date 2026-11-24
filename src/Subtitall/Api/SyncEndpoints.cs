using ElectronNET.API;
using ElectronNET.API.Entities;

namespace Subtitall.Api;

public static class SyncEndpoints
{
    public static void MapSyncApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/sync");

        // Every video with its subtitles and what is known about them: what the Sync tab lists.
        api.MapGet("/", (SyncService sync) => Results.Ok(sync.DescribeAll()));

        api.MapPost("/analyze-many", (ManyRequest request, SyncService sync) =>
            Results.Ok(new { started = sync.StartMany(request.ItemIds ?? []) }));

        // Everything the Sync tab shows about one video: its subtitles, what was found, the progress.
        api.MapGet("/{itemId}", (string itemId, SyncService sync) =>
            sync.Describe(itemId) is { } description ? Results.Ok(description) : Results.NotFound());

        api.MapPost("/analyze", (AnalyzeRequest request, SyncService sync) =>
            sync.Start(request.ItemId ?? "", request.AudioIndex) ? Results.Accepted() : Results.NotFound());

        api.MapPost("/cancel", (AnalyzeRequest request, SyncService sync) =>
        {
            // No video named: stop everything.
            if (string.IsNullOrEmpty(request.ItemId)) sync.CancelAll();
            else sync.Cancel(request.ItemId);
            return Results.NoContent();
        });

        // Subtitle files to compare besides the ones next to the video (dropped, or typed in a browser).
        api.MapPost("/add", (AddRequest request, SyncService sync) =>
        {
            var added = sync.AddExtras(request.ItemId ?? "", request.Paths ?? []);
            if (added > 0)
                sync.Start(request.ItemId ?? "");
            return Results.Ok(new { added });
        });

        api.MapPost("/browse", async (AnalyzeRequest request, SyncService sync) =>
        {
            if (!ElectronBridge.IsActive)
                return Results.Problem("File dialogs require the desktop app", statusCode: 501);
            var window = Electron.WindowManager.BrowserWindows.FirstOrDefault();
            var paths = await Electron.Dialog.ShowOpenDialogAsync(window, new OpenDialogOptions
            {
                Title = "Add subtitle files to compare",
                Properties = [OpenDialogProperty.openFile, OpenDialogProperty.multiSelections],
                Filters =
                [
                    new FileFilter { Name = "Subtitles", Extensions = ["srt", "vtt", "ass", "ssa"] },
                    new FileFilter { Name = "All files", Extensions = ["*"] },
                ],
            });
            var added = paths is { Length: > 0 } ? sync.AddExtras(request.ItemId ?? "", paths) : 0;
            if (added > 0)
                sync.Start(request.ItemId ?? "");
            return Results.Ok(new { added });
        });

        api.MapPost("/remove", (PathRequest request, SyncService sync) =>
        {
            sync.RemoveExtra(request.ItemId ?? "", request.Path ?? "");
            return Results.NoContent();
        });

        api.MapPost("/apply", (PathRequest request, SyncService sync) =>
        {
            try { return Results.Ok(sync.Apply(request.ItemId ?? "", request.Path ?? "")); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return Results.Conflict(new { message = ex.Message });
            }
        });

        api.MapPost("/undo", (PathRequest request, SyncService sync) =>
        {
            try { return Results.Ok(sync.Undo(request.ItemId ?? "", request.Path ?? "")); }
            catch (Exception ex) when (ex is InvalidOperationException or IOException or UnauthorizedAccessException)
            {
                return Results.Conflict(new { message = ex.Message });
            }
        });
    }

    public sealed record AnalyzeRequest(string? ItemId, int? AudioIndex);
    public sealed record ManyRequest(List<string>? ItemIds);
    public sealed record AddRequest(string? ItemId, List<string>? Paths);
    public sealed record PathRequest(string? ItemId, string? Path);
}
