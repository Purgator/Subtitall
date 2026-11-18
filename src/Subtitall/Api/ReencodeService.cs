using System.Collections.Concurrent;
using Subtitall.Core;
using Subtitall.Core.Reencode;

namespace Subtitall.Api;

public enum ReencodeState { Queued, Encoding, Checking, Done, Failed }

/// <summary>One file being re-encoded, or re-encoded and waiting for the user's decision.</summary>
public sealed class ReencodeJob
{
    public required string ItemId { get; init; }
    public required EncodeOptions Options { get; init; }
    public ReencodeState State { get; set; }
    public double Percent { get; set; }
    public double Speed { get; set; }
    public double? EtaSeconds { get; set; }
    public long OriginalBytes { get; set; }
    public long NewBytes { get; set; }
    public string? NewPath { get; set; }
    public double? Ssim { get; set; }
    public string? Error { get; set; }
    public CancellationTokenSource Cancellation { get; } = new();
}

/// <summary>One quality level tried on the excerpts of a file.</summary>
public sealed record TrialLevel(QualityLevel Quality, int Crf, long EstimatedBytes, double GainPercent, double? Ssim, string? Label);

/// <summary>A trial: short excerpts of one file encoded at each quality level, to choose before a batch.</summary>
public sealed class ReencodeTrial
{
    public required string ItemId { get; init; }
    public required string FileName { get; init; }
    public required string Key { get; init; }
    public required EncodeOptions Options { get; init; }
    /// <summary>running, done, failed.</summary>
    public string State { get; set; } = "running";
    public double Percent { get; set; }
    public string Step { get; set; } = "";
    public string? Error { get; set; }
    public long OriginalBytes { get; set; }
    public IReadOnlyList<double> Positions { get; set; } = [];
    public List<TrialLevel> Levels { get; } = [];
    public CancellationTokenSource Cancellation { get; } = new();
}

public sealed record ReencodeOutcome(string ItemId, bool Ok, string? Error);

/// <summary>
/// Re-encodes videos to save space, one at a time. The original is never touched: the new file is
/// written next to it (<c>name.reencoded.mkv</c>) and waits there until the user keeps it (the
/// original is deleted and the new file takes its name) or discards it.
/// </summary>
public sealed class ReencodeService
{
    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly FfmpegService _ffmpeg;
    private readonly EventBus _events;
    private readonly ILogger<ReencodeService> _logger;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, ReencodeJob> _jobs = [];
    private readonly ConcurrentDictionary<string, (long Size, DateTime Modified, VideoInfo Info)> _probes = new(StringComparer.OrdinalIgnoreCase);
    private Task? _worker;
    private ReencodeTrial? _trial;
    private int _trialCount;

    /// <summary>Excerpts and comparison pictures; nothing of value.</summary>
    private readonly string _workDirectory = Path.Combine(Path.GetTempPath(), "Subtitall", "reencode");

    public ReencodeService(
        LibraryService library, SettingsService settings, FfmpegService ffmpeg, EventBus events,
        ILogger<ReencodeService> logger, IHostApplicationLifetime lifetime)
    {
        (_library, _settings, _ffmpeg, _events, _logger) = (library, settings, ffmpeg, events, logger);
        // ffmpeg must not outlive the app.
        lifetime.ApplicationStopping.Register(() => { Cancel(null); CancelTrial(); });
        try { if (Directory.Exists(_workDirectory)) Directory.Delete(_workDirectory, recursive: true); }
        catch (Exception) { /* pictures of a previous run still open somewhere */ }
    }

    // ---- Status ------------------------------------------------------------------------------

    public object Status()
    {
        lock (_lock)
        {
            return new
            {
                running = _jobs.Values.Any(j => j.State is ReencodeState.Queued or ReencodeState.Encoding or ReencodeState.Checking),
                jobs = _jobs.Values.Select(JobDto).ToList(),
                trial = _trial is { } t ? TrialDto(t) : null,
            };
        }
    }

    private static object JobDto(ReencodeJob j) => new
    {
        itemId = j.ItemId,
        state = j.State,
        percent = Math.Round(j.Percent, 1),
        speed = j.Speed,
        etaSeconds = j.EtaSeconds,
        originalBytes = j.OriginalBytes,
        newBytes = j.NewBytes,
        gainPercent = j.State == ReencodeState.Done ? EncodePlanner.GainPercent(j.OriginalBytes, j.NewBytes) : (double?)null,
        ssim = j.Ssim,
        quality = j.Ssim is { } s ? EncodePlanner.QualityLabel(s) : null,
        error = j.Error,
        codec = j.Options.Codec,
        level = j.Options.Quality,
    };

    private static object TrialDto(ReencodeTrial t) => new
    {
        itemId = t.ItemId,
        fileName = t.FileName,
        key = t.Key,
        state = t.State,
        percent = Math.Round(t.Percent),
        step = t.Step,
        error = t.Error,
        originalBytes = t.OriginalBytes,
        positions = t.Positions,
        levels = t.Levels.ToList(),
        codec = t.Options.Codec,
    };

    private void PublishStatus() => _events.Publish("reencode-changed", Status());

    // ---- Analysis ----------------------------------------------------------------------------

    /// <summary>What each linked file is made of and whether re-encoding it is worth it.</summary>
    public async Task<IReadOnlyList<object>> AnalyzeAsync(CancellationToken ct)
    {
        var tools = await _ffmpeg.GetAsync(ct) ?? throw new InvalidOperationException("ffmpeg is not installed");
        var options = EncodeOptions.From(_settings.Current.Reencode);
        var items = _library.GetAll();
        var results = new object[items.Count];
        var adopted = false;
        await Parallel.ForEachAsync(Enumerable.Range(0, items.Count), new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = ct },
            async (index, token) =>
            {
                var item = items[index];
                adopted |= AdoptPending(item);
                var info = await ProbeAsync(tools, item.Path, token);
                var advice = info is null
                    ? new EncodeAdvice(AdviceKind.NoVideo, "This file could not be read")
                    : EncodePlanner.Advise(info, options);
                results[index] = new
                {
                    itemId = item.Id,
                    info = info is null ? null : new
                    {
                        codec = info.VideoCodec,
                        codecName = EncodePlanner.CodecName(info.VideoCodec),
                        info.Width,
                        info.Height,
                        info.BitRate,
                        info.DurationSeconds,
                        info.Hdr,
                    },
                    advice = new { advice.Kind, advice.Recommended, advice.Possible, advice.Reason },
                };
            });
        if (adopted)
            PublishStatus();
        return results;
    }

    /// <summary>
    /// A re-encoded copy left next to a file by an earlier run of the app still waits for its
    /// decision: it comes back as a finished job. Leftovers of an interrupted encode are removed.
    /// </summary>
    private bool AdoptPending(MediaItem item)
    {
        lock (_lock)
        {
            if (_jobs.ContainsKey(item.Id))
                return false;
        }
        try
        {
            var pending = EncodePlanner.PendingPath(item.Path);
            if (File.Exists(pending + ".part"))
                File.Delete(pending + ".part");
            if (!File.Exists(pending) || !File.Exists(item.Path))
                return false;
            var job = new ReencodeJob
            {
                ItemId = item.Id,
                Options = EncodeOptions.From(_settings.Current.Reencode),
                State = ReencodeState.Done,
                Percent = 100,
                OriginalBytes = new FileInfo(item.Path).Length,
                NewBytes = new FileInfo(pending).Length,
                NewPath = pending,
            };
            lock (_lock) return _jobs.TryAdd(item.Id, job);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    private async Task<VideoInfo?> ProbeAsync(FfmpegTools tools, string path, CancellationToken ct)
    {
        FileInfo file;
        try
        {
            file = new FileInfo(path);
            if (!file.Exists)
                return null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return null;
        }
        if (_probes.TryGetValue(path, out var cached) && cached.Size == file.Length && cached.Modified == file.LastWriteTimeUtc)
            return cached.Info;

        var output = await FfmpegRunner.RunAsync(tools.Ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], ct);
        if (output.ExitCode != 0)
            return null;
        try
        {
            var info = VideoInfo.Parse(output.StdOut);
            if (info.SizeBytes <= 0)
                info = info with { SizeBytes = file.Length };
            _probes[path] = (file.Length, file.LastWriteTimeUtc, info);
            return info;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    // ---- Batch -------------------------------------------------------------------------------

    /// <summary>Queues files for re-encoding with the current settings. Returns how many were queued.</summary>
    public int Start(IReadOnlyList<string> itemIds, QualityLevel? quality)
    {
        var options = EncodeOptions.From(_settings.Current.Reencode);
        if (quality is { } q)
            options = options with { Quality = q };

        var queued = 0;
        lock (_lock)
        {
            foreach (var id in itemIds.Distinct())
            {
                if (_library.Get(id) is not { } item)
                    continue;
                // A file being encoded or waiting for a decision is left alone; a failed one may run again.
                if (_jobs.TryGetValue(id, out var existing) && existing.State != ReencodeState.Failed)
                    continue;
                _jobs[id] = new ReencodeJob { ItemId = id, Options = options, State = ReencodeState.Queued, OriginalBytes = item.SizeBytes };
                queued++;
            }
            if (queued > 0 && _worker is null or { IsCompleted: true })
                _worker = Task.Run(WorkAsync);
        }
        if (queued > 0)
            PublishStatus();
        return queued;
    }

    /// <summary>Stops one encode (or all of them); what was written so far is deleted.</summary>
    public void Cancel(string? itemId)
    {
        lock (_lock)
        {
            foreach (var job in _jobs.Values.Where(j => itemId is null || j.ItemId == itemId).ToList())
            {
                if (job.State == ReencodeState.Queued)
                    _jobs.Remove(job.ItemId);
                else if (job.State is ReencodeState.Encoding or ReencodeState.Checking)
                    job.Cancellation.Cancel();
            }
        }
        PublishStatus();
    }

    private async Task WorkAsync()
    {
        while (true)
        {
            ReencodeJob? job;
            lock (_lock)
            {
                job = _jobs.Values.FirstOrDefault(j => j.State == ReencodeState.Queued);
                if (job is null)
                {
                    _worker = null;
                    return;
                }
                job.State = ReencodeState.Encoding;
            }
            PublishStatus();

            try
            {
                await EncodeAsync(job, job.Cancellation.Token);
            }
            catch (OperationCanceledException)
            {
                lock (_lock) _jobs.Remove(job.ItemId);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Re-encoding item {Item} failed", job.ItemId);
                lock (_lock) (job.State, job.Error) = (ReencodeState.Failed, ex.Message);
            }
            PublishStatus();
        }
    }

    private async Task EncodeAsync(ReencodeJob job, CancellationToken ct)
    {
        var tools = await _ffmpeg.GetAsync(ct) ?? throw new InvalidOperationException("ffmpeg is not installed");
        var item = _library.Get(job.ItemId) ?? throw new InvalidOperationException("The file is no longer in the list");
        var source = item.Path;
        var info = await ProbeAsync(tools, source, ct) ?? throw new InvalidOperationException("This file could not be read");
        if (!info.HasVideo)
            throw new InvalidOperationException("No video track found");
        if (!tools.Encoders.Contains(EncodePlanner.Encoder(job.Options.Codec)))
            throw new InvalidOperationException($"This ffmpeg cannot encode {job.Options.Codec} ({EncodePlanner.Encoder(job.Options.Codec)} is missing)");
        job.OriginalBytes = info.SizeBytes;
        EnsureFreeSpace(source, info.SizeBytes);

        var pending = EncodePlanner.PendingPath(source);
        var part = pending + ".part";
        try
        {
            var reader = new FfmpegRunner.ProgressReader();
            var started = DateTime.UtcNow;
            var lastReport = DateTime.MinValue;
            var result = await FfmpegRunner.RunAsync(tools.Ffmpeg, EncodePlanner.BuildEncodeArgs(info, job.Options, source, part), ct,
                line =>
                {
                    if (reader.Read(line) is not { } progress || info.DurationSeconds <= 0)
                        return;
                    job.Percent = Math.Clamp(progress.Seconds / info.DurationSeconds * 100, 0, 99.9);
                    job.Speed = progress.Speed;
                    // Measured on the wall clock: ffmpeg's own speed figure jumps around.
                    var elapsed = (DateTime.UtcNow - started).TotalSeconds;
                    job.EtaSeconds = job.Percent > 1 && elapsed > 5 ? elapsed * (100 - job.Percent) / job.Percent : null;
                    if ((DateTime.UtcNow - lastReport).TotalMilliseconds < 700)
                        return;
                    lastReport = DateTime.UtcNow;
                    _events.Publish("reencode-progress", new { itemId = job.ItemId, percent = Math.Round(job.Percent, 1), job.Speed, job.EtaSeconds });
                }, background: true);

            if (result.ExitCode != 0)
                throw new InvalidOperationException(result.LastError.Length > 0 ? result.LastError : $"ffmpeg stopped with code {result.ExitCode}");

            // A new file that is shorter than the original must never be offered as its replacement.
            var encoded = await ProbeAsync(tools, part, ct);
            if (encoded is null || !encoded.HasVideo || !EncodePlanner.DurationMatches(info.DurationSeconds, encoded.DurationSeconds))
                throw new InvalidOperationException("The new file came out incomplete — the original was kept");

            File.Move(part, pending, overwrite: true);
        }
        finally
        {
            _probes.TryRemove(part, out _);
            try { if (File.Exists(part)) File.Delete(part); }
            catch (Exception ex) { _logger.LogDebug(ex, "Could not delete {File}", part); }
        }

        lock (_lock)
        {
            job.NewPath = pending;
            job.NewBytes = new FileInfo(pending).Length;
            job.Percent = 100;
            job.EtaSeconds = null;
            job.State = ReencodeState.Checking;
        }
        PublishStatus();

        try
        {
            job.Ssim = await MeasureAsync(tools, info, source, pending, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled during the quality check: the encode itself is complete, keep it.
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Quality check failed for {File}", source);
        }
        lock (_lock) job.State = ReencodeState.Done;
    }

    private static void EnsureFreeSpace(string path, long needed)
    {
        if (!OperatingSystem.IsWindows())
            return;
        try
        {
            var root = Path.GetPathRoot(path);
            if (string.IsNullOrEmpty(root) || root.StartsWith(@"\\", StringComparison.Ordinal))
                return;
            if (new DriveInfo(root).AvailableFreeSpace < needed)
                throw new InvalidOperationException($"Not enough free space on {root} — the new file is written next to the original until you decide");
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or UnauthorizedAccessException)
        {
            // Can't tell — let the encode find out.
        }
    }

    /// <summary>Picture similarity (SSIM) between two files, averaged over a few excerpts.</summary>
    private static async Task<double?> MeasureAsync(FfmpegTools tools, VideoInfo info, string original, string encoded, CancellationToken ct)
    {
        var (starts, length) = EncodePlanner.SamplePositions(info.DurationSeconds);
        var scores = new List<double>();
        foreach (var start in starts)
            if (await SsimAsync(tools, info, original, start, encoded, start, length, ct) is { } score)
                scores.Add(score);
        return scores.Count > 0 ? Math.Round(scores.Average(), 4) : null;
    }

    private static async Task<double?> SsimAsync(
        FfmpegTools tools, VideoInfo info, string original, double originalStart, string encoded, double encodedStart, double length, CancellationToken ct)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-nostats", "-loglevel", "info",
            "-ss", EncodePlanner.Seconds(originalStart), "-t", EncodePlanner.Seconds(length), "-i", original,
            "-ss", EncodePlanner.Seconds(encodedStart), "-t", EncodePlanner.Seconds(length), "-i", encoded,
            "-lavfi", $"[0:V:0]setpts=PTS-STARTPTS[ref];[1:V:0]setpts=PTS-STARTPTS,scale={info.Width}:{info.Height}:flags=bicubic[enc];[enc][ref]ssim",
            "-an", "-sn", "-dn", "-f", "null", "-",
        };
        var result = await FfmpegRunner.RunAsync(tools.Ffmpeg, args, ct, background: true);
        return result.ExitCode == 0 ? FfmpegRunner.ParseSsim(result.StdErr) : null;
    }

    // ---- Keep / discard ----------------------------------------------------------------------

    /// <summary>Keeps the new files: each original is deleted and the new file takes its name.</summary>
    public IReadOnlyList<ReencodeOutcome> Apply(IReadOnlyList<string> itemIds)
    {
        var outcomes = new List<ReencodeOutcome>();
        foreach (var id in itemIds.Distinct())
        {
            ReencodeJob? job;
            lock (_lock) _jobs.TryGetValue(id, out job);
            if (job is not { State: ReencodeState.Done, NewPath: { } newPath })
                continue;
            try
            {
                var item = _library.Get(id) ?? throw new InvalidOperationException("The file is no longer in the list");
                if (!File.Exists(newPath))
                    throw new InvalidOperationException("The new file is gone — the original was kept");
                var source = item.Path;
                var final = EncodePlanner.FinalPath(source);
                if (string.Equals(final, source, StringComparison.OrdinalIgnoreCase))
                {
                    File.Move(newPath, source, overwrite: true);
                }
                else
                {
                    // Another extension (video.avi → video.mkv): never overwrite a file that is already there.
                    if (File.Exists(final))
                        throw new InvalidOperationException($"{Path.GetFileName(final)} already exists in this folder");
                    File.Move(newPath, final);
                    File.Delete(source);
                }
                _probes.TryRemove(source, out _);
                _library.Replace(item, final);
                Forget(id);
                outcomes.Add(new ReencodeOutcome(id, true, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or InvalidOperationException)
            {
                outcomes.Add(new ReencodeOutcome(id, false, ex.Message));
            }
        }
        if (outcomes.Count > 0)
        {
            _events.Publish("library-changed");
            PublishStatus();
        }
        return outcomes;
    }

    /// <summary>Discards the new files; the originals stay as they were. Also clears failed jobs.</summary>
    public IReadOnlyList<ReencodeOutcome> Revert(IReadOnlyList<string> itemIds)
    {
        var outcomes = new List<ReencodeOutcome>();
        foreach (var id in itemIds.Distinct())
        {
            ReencodeJob? job;
            lock (_lock) _jobs.TryGetValue(id, out job);
            if (job is not { State: ReencodeState.Done or ReencodeState.Failed })
                continue;
            try
            {
                if (job.NewPath is { } newPath && File.Exists(newPath))
                    File.Delete(newPath);
                Forget(id);
                outcomes.Add(new ReencodeOutcome(id, true, null));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                outcomes.Add(new ReencodeOutcome(id, false, ex.Message));
            }
        }
        if (outcomes.Count > 0)
            PublishStatus();
        return outcomes;
    }

    private void Forget(string itemId)
    {
        lock (_lock) _jobs.Remove(itemId);
        DeleteQuietly(Path.Combine(_workDirectory, $"item-{itemId}"));
    }

    private void DeleteQuietly(string directory)
    {
        try { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
        catch (Exception ex) { _logger.LogDebug(ex, "Could not delete {Directory}", directory); }
    }

    // ---- Comparison pictures -----------------------------------------------------------------

    /// <summary>
    /// Takes the same frames out of the original and of the new file of a finished job, for the
    /// before/after view. Returns the key and the positions (seconds) of the pictures.
    /// </summary>
    public async Task<object?> CompareAsync(string itemId, CancellationToken ct)
    {
        ReencodeJob? job;
        lock (_lock) _jobs.TryGetValue(itemId, out job);
        var item = _library.Get(itemId);
        if (job is not { State: ReencodeState.Done, NewPath: { } newPath } || item is null)
            return null;
        var tools = await _ffmpeg.GetAsync(ct) ?? throw new InvalidOperationException("ffmpeg is not installed");
        var info = await ProbeAsync(tools, item.Path, ct) ?? throw new InvalidOperationException("This file could not be read");

        var key = $"item-{itemId}";
        var directory = Path.Combine(_workDirectory, key);
        Directory.CreateDirectory(directory);
        var (starts, length) = EncodePlanner.SamplePositions(info.DurationSeconds);
        var positions = starts.Select(s => s + length / 2).ToList();
        for (var i = 0; i < positions.Count; i++)
        {
            await FrameAsync(tools, item.Path, positions[i], Path.Combine(directory, $"source-{i}.png"), ct);
            await FrameAsync(tools, newPath, positions[i], Path.Combine(directory, $"new-{i}.png"), ct);
        }
        return new { key, positions, stamp = DateTime.UtcNow.Ticks };
    }

    private static async Task FrameAsync(FfmpegTools tools, string file, double position, string target, CancellationToken ct)
    {
        if (File.Exists(target))
            return;
        var result = await FfmpegRunner.RunAsync(tools.Ffmpeg,
        [
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-ss", EncodePlanner.Seconds(position), "-i", file,
            "-map", "0:V:0", "-frames:v", "1", "-vf", "scale='min(1920,iw)':-2", "-update", "1", target,
        ], ct, background: true);
        if (result.ExitCode != 0)
            throw new InvalidOperationException($"Could not take a picture of the video: {result.LastError}");
    }

    /// <summary>The file of a comparison picture, or null when the names are not ones this service wrote.</summary>
    public string? FramePath(string key, string name)
    {
        if (!System.Text.RegularExpressions.Regex.IsMatch(key, "^[a-z0-9-]+$")
            || !System.Text.RegularExpressions.Regex.IsMatch(name, @"^[a-z0-9-]+\.png$"))
            return null;
        var path = Path.Combine(_workDirectory, key, name);
        return File.Exists(path) ? path : null;
    }

    // ---- Trial -------------------------------------------------------------------------------

    /// <summary>
    /// Starts a trial on one file: a few short excerpts are encoded at each quality level, giving
    /// an estimated gain, a quality score and before/after pictures. False when something else runs.
    /// </summary>
    public bool StartTrial(string itemId)
    {
        ReencodeTrial trial;
        lock (_lock)
        {
            if (_trial is { State: "running" } || _worker is { IsCompleted: false })
                return false;
            if (_library.Get(itemId) is not { } item)
                return false;
            trial = _trial = new ReencodeTrial
            {
                ItemId = itemId,
                FileName = item.FileName,
                Key = $"trial-{++_trialCount}",
                Options = EncodeOptions.From(_settings.Current.Reencode),
                OriginalBytes = item.SizeBytes,
                Step = "Reading the file…",
            };
        }
        PublishStatus();
        _ = Task.Run(async () =>
        {
            try
            {
                await RunTrialAsync(trial, trial.Cancellation.Token);
                trial.State = "done";
            }
            catch (OperationCanceledException)
            {
                lock (_lock) { if (_trial == trial) _trial = null; }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Trial failed for item {Item}", trial.ItemId);
                (trial.State, trial.Error) = ("failed", ex.Message);
            }
            PublishStatus();
        });
        return true;
    }

    public void CancelTrial()
    {
        lock (_lock)
        {
            if (_trial is { State: "running" } running)
                running.Cancellation.Cancel();
            else
                _trial = null;
        }
        PublishStatus();
    }

    private async Task RunTrialAsync(ReencodeTrial trial, CancellationToken ct)
    {
        var tools = await _ffmpeg.GetAsync(ct) ?? throw new InvalidOperationException("ffmpeg is not installed");
        var item = _library.Get(trial.ItemId) ?? throw new InvalidOperationException("The file is no longer in the list");
        var info = await ProbeAsync(tools, item.Path, ct) ?? throw new InvalidOperationException("This file could not be read");
        if (!info.HasVideo)
            throw new InvalidOperationException("No video track found");
        if (!tools.Encoders.Contains(EncodePlanner.Encoder(trial.Options.Codec)))
            throw new InvalidOperationException($"This ffmpeg cannot encode {trial.Options.Codec} ({EncodePlanner.Encoder(trial.Options.Codec)} is missing)");

        foreach (var old in Directory.Exists(_workDirectory) ? Directory.GetDirectories(_workDirectory, "trial-*") : [])
            DeleteQuietly(old);
        var directory = Path.Combine(_workDirectory, trial.Key);
        Directory.CreateDirectory(directory);

        var (starts, length) = EncodePlanner.SamplePositions(info.DurationSeconds);
        var levels = new[] { QualityLevel.High, QualityLevel.Balanced, QualityLevel.Compact };
        trial.OriginalBytes = info.SizeBytes;
        trial.Positions = starts.Select(s => s + length / 2).ToList();

        var steps = starts.Count * (1 + levels.Length * 2);
        var done = 0;
        void Advance(string step)
        {
            trial.Percent = done++ * 100.0 / steps;
            trial.Step = step;
            _events.Publish("reencode-trial", new { percent = Math.Round(trial.Percent), step });
        }

        // How much the excerpts weigh in the original: video alone, and all tracks together.
        long sourceVideo = 0, sourceAll = 0;
        for (var i = 0; i < starts.Count; i++)
        {
            Advance("Measuring the original…");
            var packets = await FfmpegRunner.RunAsync(tools.Ffprobe,
            [
                // An absolute end: ffprobe starts at the keyframe before the excerpt, and a relative
                // length ("%+8") would be counted from there — ending before the excerpt is over.
                "-v", "error", "-read_intervals", $"{EncodePlanner.Seconds(starts[i])}%{EncodePlanner.Seconds(starts[i] + length)}",
                "-show_entries", "packet=stream_index,pts_time,size", "-of", "csv=p=0", item.Path,
            ], ct);
            var (video, all) = FfmpegRunner.SumPackets(packets.StdOut, info.VideoStreamIndex, starts[i], length);
            sourceVideo += video;
            sourceAll += all;
            await FrameAsync(tools, item.Path, trial.Positions[i], Path.Combine(directory, $"source-{i}.png"), ct);
        }

        foreach (var level in levels)
        {
            var options = trial.Options with { Quality = level };
            var name = level.ToString().ToLowerInvariant();
            long encodedBytes = 0;
            var scores = new List<double>();
            for (var i = 0; i < starts.Count; i++)
            {
                Advance($"Encoding excerpt {i + 1} of {starts.Count} — {LevelName(level)}…");
                var sample = Path.Combine(directory, $"{name}-{i}.mkv");
                var result = await FfmpegRunner.RunAsync(tools.Ffmpeg,
                    EncodePlanner.BuildSampleArgs(info, options, item.Path, starts[i], length, sample), ct, background: true);
                if (result.ExitCode != 0)
                    throw new InvalidOperationException(result.LastError.Length > 0 ? result.LastError : "ffmpeg could not encode an excerpt");
                encodedBytes += new FileInfo(sample).Length;

                Advance($"Comparing with the original — {LevelName(level)}…");
                if (await SsimAsync(tools, info, item.Path, starts[i], sample, 0, length, ct) is { } score)
                    scores.Add(score);
                await FrameAsync(tools, sample, length / 2, Path.Combine(directory, $"{name}-{i}.png"), ct);
                try { File.Delete(sample); } catch (IOException) { }
            }

            long estimated;
            if (sourceVideo > 0 && sourceAll > 0)
                estimated = EncodePlanner.EstimateSize(info.SizeBytes, (double)sourceVideo / sourceAll, (double)encodedBytes / sourceVideo);
            else // packet sizes unavailable: compare bitrates, as if the file were video only
                estimated = info.BitRate > 0
                    ? EncodePlanner.EstimateSize(info.SizeBytes, 1, encodedBytes * 8.0 / (length * starts.Count) / info.BitRate)
                    : info.SizeBytes;
            double? ssim = scores.Count > 0 ? Math.Round(scores.Average(), 4) : null;
            lock (_lock)
            {
                trial.Levels.Add(new TrialLevel(level, EncodePlanner.Crf(options.Codec, level), estimated,
                    EncodePlanner.GainPercent(info.SizeBytes, estimated), ssim, ssim is { } s ? EncodePlanner.QualityLabel(s) : null));
            }
            PublishStatus();
        }
        trial.Percent = 100;
        trial.Step = "";
    }

    private static string LevelName(QualityLevel level) => level switch
    {
        QualityLevel.High => "best quality",
        QualityLevel.Balanced => "balanced",
        _ => "smallest",
    };
}
