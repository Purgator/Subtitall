using Subtitall.Core.Reencode;

namespace Subtitall.Api;

public static class ReencodeEndpoints
{
    public static void MapReencodeApi(this WebApplication app)
    {
        var api = app.MapGroup("/api/reencode");

        // Everything the Re-encode tab shows that can change by itself: ffmpeg, jobs, trial.
        api.MapGet("/status", async (FfmpegService ffmpeg, ReencodeService reencode, CancellationToken ct) =>
            Results.Ok(new { ffmpeg = await ffmpeg.GetStatusAsync(ct), state = reencode.Status() }));

        // ---- ffmpeg ----------------------------------------------------------------------------
        api.MapPost("/ffmpeg/install", (FfmpegService ffmpeg) =>
            ffmpeg.StartInstall()
                ? Results.Accepted()
                : Results.Conflict(new { message = "ffmpeg is already being downloaded" }));

        api.MapPost("/ffmpeg/cancel", (FfmpegService ffmpeg) =>
        {
            ffmpeg.CancelInstall();
            return Results.NoContent();
        });

        // The user installed ffmpeg by other means: look for it again.
        api.MapPost("/ffmpeg/recheck", async (FfmpegService ffmpeg, CancellationToken ct) =>
        {
            ffmpeg.Forget();
            return Results.Ok(await ffmpeg.GetStatusAsync(ct));
        });

        // The download the system refuses to start: delete it.
        api.MapPost("/ffmpeg/remove", async (FfmpegService ffmpeg, CancellationToken ct) =>
        {
            ffmpeg.RemoveDownload();
            return Results.Ok(await ffmpeg.GetStatusAsync(ct));
        });

        // ---- Files -----------------------------------------------------------------------------
        api.MapPost("/analyze", async (ReencodeService reencode, CancellationToken ct) =>
        {
            try { return Results.Ok(await reencode.AnalyzeAsync(ct)); }
            catch (InvalidOperationException ex) { return Results.Conflict(new { message = ex.Message }); }
        });

        api.MapPost("/start", (StartRequest request, ReencodeService reencode) =>
            Results.Ok(new { queued = reencode.Start(request.ItemIds ?? [], request.Quality) }));

        api.MapPost("/cancel", (CancelRequest request, ReencodeService reencode) =>
        {
            reencode.Cancel(request.ItemId);
            return Results.NoContent();
        });

        api.MapPost("/apply", (ItemsRequest request, ReencodeService reencode) =>
            Results.Ok(reencode.Apply(request.ItemIds ?? [])));

        api.MapPost("/revert", (ItemsRequest request, ReencodeService reencode) =>
            Results.Ok(reencode.Revert(request.ItemIds ?? [])));

        // ---- Trial and comparison --------------------------------------------------------------
        api.MapPost("/trial", (TrialRequest request, ReencodeService reencode) =>
            reencode.StartTrial(request.ItemId ?? "")
                ? Results.Accepted()
                : Results.Conflict(new { message = "Wait for the running encodes to finish before testing another file" }));

        api.MapPost("/trial/cancel", (ReencodeService reencode) =>
        {
            reencode.CancelTrial();
            return Results.NoContent();
        });

        api.MapGet("/compare", async (string itemId, ReencodeService reencode, CancellationToken ct) =>
        {
            try
            {
                return await reencode.CompareAsync(itemId, ct) is { } comparison ? Results.Ok(comparison) : Results.NotFound();
            }
            catch (InvalidOperationException ex)
            {
                return Results.Conflict(new { message = ex.Message });
            }
        });

        api.MapGet("/frame/{key}/{name}", (string key, string name, ReencodeService reencode) =>
            reencode.FramePath(key, name) is { } path ? Results.File(path, "image/png") : Results.NotFound());
    }

    public sealed record StartRequest(List<string>? ItemIds, QualityLevel? Quality);
    public sealed record CancelRequest(string? ItemId);
    public sealed record ItemsRequest(List<string>? ItemIds);
    public sealed record TrialRequest(string? ItemId);
}
