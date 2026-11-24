using System.Collections.Concurrent;
using System.Text;
using Subtitall.Core;
using Subtitall.Core.Reencode;
using Subtitall.Core.Sync;

namespace Subtitall.Api;

/// <summary>One subtitle file of a video, with what the last analysis found out about it.</summary>
internal sealed record SyncResult(long Size, DateTime Modified, int AudioIndex, SyncAnalysis? Analysis, string? Error);

/// <summary>A fix that was applied and can be undone: the file it wrote, the backup it made, and where it came from.</summary>
internal sealed record AppliedFix(string Target, string? Backup, string? Source);

internal sealed class SyncSession
{
    public required string ItemId { get; init; }
    /// <summary>Subtitle files the user added from elsewhere than next to the video.</summary>
    public List<string> Extras { get; } = [];
    public string State { get; set; } = "idle"; // idle, queued, running, done, failed
    public double Percent { get; set; }
    public string Step { get; set; } = "";
    public string? Error { get; set; }
    public IReadOnlyList<AudioTrack> Tracks { get; set; } = [];
    public int AudioIndex { get; set; } = -1;   // -1: the preferred track
    public double Duration { get; set; }
    public double SpeechRatio { get; set; }
    public Dictionary<string, SyncResult> Results { get; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, AppliedFix> Applied { get; } = new(StringComparer.OrdinalIgnoreCase);
    public CancellationTokenSource? Cancellation { get; set; }
}

/// <summary>
/// The Sync tab's engine: listens to a video's audio once, then compares every subtitle file it has —
/// the ones next to the video and the ones the user added — with the speech, ranks them, and
/// writes a corrected file (the original kept as <c>.bak</c>) when the user accepts a fix.
/// </summary>
public sealed class SyncService
{
    private static readonly string[] CandidateExtensions = [".srt", ".vtt", ".ass", ".ssa", ".sub"];

    private readonly LibraryService _library;
    private readonly SettingsService _settings;
    private readonly FfmpegService _ffmpeg;
    private readonly EventBus _events;
    private readonly ILogger<SyncService> _logger;

    private readonly Lock _lock = new();
    private readonly Dictionary<string, SyncSession> _sessions = [];
    private readonly SemaphoreSlim _gate = new(1, 1); // one decode at a time
    private readonly ConcurrentDictionary<string, float[]> _speech = new();
    private readonly ConcurrentDictionary<string, (IReadOnlyList<AudioTrack> Tracks, double Duration)> _probes = new();

    public SyncService(
        LibraryService library, SettingsService settings, FfmpegService ffmpeg, EventBus events,
        ILogger<SyncService> logger, IHostApplicationLifetime lifetime)
    {
        (_library, _settings, _ffmpeg, _events, _logger) = (library, settings, ffmpeg, events, logger);
        // ffmpeg must not outlive the app.
        lifetime.ApplicationStopping.Register(() =>
        {
            lock (_lock)
                foreach (var session in _sessions.Values)
                    session.Cancellation?.Cancel();
        });
    }

    // ---- What the UI sees ----------------------------------------------------------------------

    public object? Describe(string itemId)
    {
        var item = _library.Get(itemId);
        if (item is null)
            return null;
        var session = SessionFor(itemId);

        var listed = Candidates(item, session);
        lock (session)
        {
            var rows = listed.Select(c =>
            {
                var result = session.Results.GetValueOrDefault(c.Path) is { } r && Current(r, c.Path) ? r : null;
                return (c, result);
            }).ToList();
            var best = SyncRanking.PickBest(rows, r => r.c.Language, r => r.result?.Analysis);

            var candidates = rows
                .OrderByDescending(r => r.result?.Analysis?.RankScore ?? -1)
                .ThenBy(r => r.c.Name, StringComparer.OrdinalIgnoreCase)
                .Select(r =>
                {
                    var analysis = r.result?.Analysis;
                    var action = analysis?.Verdict switch
                    {
                        SyncVerdict.Shift or SyncVerdict.Rescale => "fix",
                        SyncVerdict.InSync when !r.c.Companion => "use",
                        _ => null,
                    };
                    return new
                    {
                        path = r.c.Path,
                        name = r.c.Name,
                        folder = r.c.Companion ? null : Path.GetDirectoryName(r.c.Path),
                        language = r.c.Language,
                        source = r.c.Companion ? "folder" : "added",
                        size = r.c.Size,
                        analyzed = r.result is not null,
                        analysis,
                        error = r.result?.Error,
                        best = best.Contains(r),
                        action,
                        // What an added subtitle would overwrite when saved next to the video.
                        replaces = !r.c.Companion && action is not null && File.Exists(TargetFor(item, r.c))
                            ? Path.GetFileName(TargetFor(item, r.c))
                            : null,
                        canUndo = session.Applied.ContainsKey(r.c.Path) || File.Exists(r.c.Path + ".bak"),
                    };
                })
                .ToList();

            return new
            {
                itemId,
                state = session.State,
                percent = Math.Round(session.Percent, 1),
                step = session.Step,
                error = session.Error,
                audioTracks = session.Tracks.Select(t => new { index = t.Index, label = t.Label }).ToList(),
                audioIndex = session.Tracks.Count == 0 ? -1 : ChosenTrack(session),
                durationSeconds = session.Duration,
                speechPercent = Math.Round(session.SpeechRatio * 100),
                candidates,
            };
        }
    }

    /// <summary>Every video of the library, as <see cref="Describe"/> tells it (what the Sync tab lists).</summary>
    public IReadOnlyList<object> DescribeAll() =>
        _library.GetAll().Select(i => Describe(i.Id)).OfType<object>().ToList();

    private void Publish(string itemId)
    {
        if (Describe(itemId) is { } description)
            _events.Publish("sync-changed", description);
    }

    // ---- Candidates ----------------------------------------------------------------------------

    private sealed record Candidate(string Path, string Name, string? Language, bool Companion, long Size);

    // Paths come back from the UI as the server wrote them, but be forgiving about slashes and case.
    private static string Normalize(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException) { return path; }
    }

    internal SyncSession SessionFor(string itemId)
    {
        lock (_lock)
        {
            if (!_sessions.TryGetValue(itemId, out var session))
                _sessions[itemId] = session = new SyncSession { ItemId = itemId };
            return session;
        }
    }

    private static List<Candidate> Candidates(MediaItem item, SyncSession session)
    {
        var companions = SubtitleNaming.FindCompanionFiles(item.Path)
            .Where(f => CandidateExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase))
            .ToList();
        List<string> extras;
        lock (session)
            extras = [.. session.Extras];

        var list = new List<Candidate>();
        foreach (var path in companions.Select(p => (Path: p, Companion: true)).Concat(extras.Select(p => (Path: p, Companion: false))))
        {
            if (list.Any(c => string.Equals(c.Path, path.Path, StringComparison.OrdinalIgnoreCase)))
                continue;
            try
            {
                var info = new FileInfo(path.Path);
                if (info.Exists)
                    list.Add(new Candidate(path.Path, info.Name, SubtitleNaming.GuessLanguage(path.Path), path.Companion, info.Length));
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // unreadable: not a candidate
            }
        }
        return list;
    }

    /// <summary>Where a subtitle ends up when it is applied: itself when it sits next to the video, else "video.lang.ext".</summary>
    private string TargetFor(MediaItem item, Candidate candidate) =>
        candidate.Companion
            ? candidate.Path
            : SubtitleNaming.GetSubtitlePath(item.Path, candidate.Language ?? _settings.Current.Languages.FirstOrDefault() ?? "en", Path.GetExtension(candidate.Path));

    private static bool Current(SyncResult result, string path)
    {
        try
        {
            var info = new FileInfo(path);
            return info.Exists && info.Length == result.Size && info.LastWriteTimeUtc == result.Modified;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>Adds subtitle files (or the subtitle files of a folder) to compare. Returns how many are new.</summary>
    public int AddExtras(string itemId, IEnumerable<string> paths)
    {
        var item = _library.Get(itemId);
        if (item is null)
            return 0;
        var session = SessionFor(itemId);
        var known = Candidates(item, session).Select(c => c.Path).ToList();
        var added = 0;
        foreach (var raw in paths)
        {
            IEnumerable<string> files;
            try
            {
                var full = Path.GetFullPath(raw);
                files = Directory.Exists(full) ? Directory.EnumerateFiles(full).ToList() : [full];
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or ArgumentException)
            {
                continue;
            }
            foreach (var file in files.Where(f => CandidateExtensions.Contains(Path.GetExtension(f), StringComparer.OrdinalIgnoreCase) && File.Exists(f)))
            {
                if (known.Contains(file, StringComparer.OrdinalIgnoreCase))
                    continue;
                lock (session)
                    session.Extras.Add(file);
                known.Add(file);
                added++;
            }
        }
        return added;
    }

    public void RemoveExtra(string itemId, string path)
    {
        path = Normalize(path);
        var session = SessionFor(itemId);
        lock (session)
        {
            session.Extras.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            session.Results.Remove(path);
        }
        Publish(itemId);
    }

    // ---- Analysis ------------------------------------------------------------------------------

    /// <summary>Starts (or restarts) the analysis of a video. False when the video is not in the library.</summary>
    public bool Start(string itemId, int? audioIndex = null)
    {
        var item = _library.Get(itemId);
        if (item is null)
            return false;
        var session = SessionFor(itemId);
        CancellationTokenSource cts;
        lock (session)
        {
            session.Cancellation?.Cancel();
            cts = session.Cancellation = new CancellationTokenSource();
            // One video is listened to at a time: the others wait their turn (see RunAsync).
            (session.State, session.Percent, session.Step, session.Error) = ("queued", 0, "Waiting for the other videos…", null);
            if (audioIndex is { } index && index != session.AudioIndex)
            {
                session.AudioIndex = index;
                session.Results.Clear(); // measured against another track
            }
        }
        Publish(itemId);
        _ = Task.Run(() => RunAsync(item, session, cts), CancellationToken.None);
        return true;
    }

    /// <summary>Starts the comparison of several videos, one after the other. Returns how many were started.</summary>
    public int StartMany(IEnumerable<string> itemIds) => itemIds.Distinct().Count(id => Start(id));

    public void Cancel(string itemId)
    {
        var session = SessionFor(itemId);
        lock (session)
            session.Cancellation?.Cancel();
    }

    /// <summary>Stops everything that runs or waits.</summary>
    public void CancelAll()
    {
        List<SyncSession> sessions;
        lock (_lock)
            sessions = [.. _sessions.Values];
        foreach (var session in sessions)
            lock (session)
                session.Cancellation?.Cancel();
    }

    private static int ChosenTrack(SyncSession session) =>
        session.AudioIndex >= 0 && session.AudioIndex < session.Tracks.Count ? session.AudioIndex : AudioTrack.Preferred(session.Tracks);

    private void Progress(SyncSession session, CancellationTokenSource cts, double percent, string step)
    {
        bool changed;
        lock (session)
        {
            if (session.Cancellation != cts)
                return;
            changed = Math.Abs(percent - session.Percent) >= 1 || step != session.Step;
            (session.Percent, session.Step) = (percent, step);
        }
        if (changed)
            _events.Publish("sync-progress", new { itemId = session.ItemId, percent = Math.Round(percent, 1), step });
    }

    private async Task RunAsync(MediaItem item, SyncSession session, CancellationTokenSource cts)
    {
        var ct = cts.Token;
        try
        {
            await _gate.WaitAsync(ct);
            try
            {
                lock (session)
                {
                    if (session.Cancellation != cts) return;
                    (session.State, session.Step) = ("running", "Starting…");
                }
                Publish(item.Id);
                var tools = await _ffmpeg.GetAsync(ct) ?? throw new InvalidOperationException("ffmpeg is not installed");

                Progress(session, cts, 1, "Reading the video…");
                var (tracks, duration) = await ProbeAsync(tools, item.Path, ct);
                if (tracks.Count == 0)
                    throw new InvalidOperationException("This video has no audio track");
                int audioIndex;
                lock (session)
                {
                    session.Tracks = tracks;
                    audioIndex = ChosenTrack(session);
                }
                Publish(item.Id);

                var speech = await SpeechAsync(tools, item.Path, audioIndex, duration, (p, step) => Progress(session, cts, p, step), ct);
                var analyzer = new SyncAnalyzer(speech);
                lock (session)
                    (session.Duration, session.SpeechRatio) = (analyzer.DurationSeconds, analyzer.SpeechRatio);

                var candidates = Candidates(item, session);
                for (var i = 0; i < candidates.Count; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var candidate = candidates[i];
                    Progress(session, cts, 85 + 15.0 * i / candidates.Count, $"Comparing {candidate.Name}…");
                    CompareOne(analyzer, session, candidate, audioIndex);
                    Publish(item.Id);
                }
            }
            finally
            {
                _gate.Release();
            }

            lock (session)
            {
                if (session.Cancellation != cts) return;
                (session.State, session.Percent, session.Step) = ("done", 100, "");
            }
        }
        catch (OperationCanceledException)
        {
            lock (session)
            {
                if (session.Cancellation != cts) return; // replaced by a newer run, which reports for itself
                (session.State, session.Step) = ("idle", "");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Sync analysis of {Path} failed", item.Path);
            lock (session)
            {
                if (session.Cancellation != cts) return;
                (session.State, session.Error, session.Step) = ("failed", ex.Message, "");
            }
        }
        Publish(item.Id);
    }

    private void CompareOne(SyncAnalyzer analyzer, SyncSession session, Candidate candidate, int audioIndex)
    {
        FileInfo info;
        try { info = new FileInfo(candidate.Path); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return; }

        lock (session)
        {
            if (session.Results.GetValueOrDefault(candidate.Path) is { } known && known.AudioIndex == audioIndex && Current(known, candidate.Path))
                return; // nothing changed since the last comparison
        }

        SyncAnalysis? analysis = null;
        string? error = null;
        var extension = Path.GetExtension(candidate.Path);
        try
        {
            if (!SubtitleParser.IsSupported(extension))
            {
                error = $"{extension} files cannot be read yet (use SRT, VTT or ASS)";
            }
            else if (info.Length > 8 * 1024 * 1024)
            {
                error = "This file is too big to be a subtitle";
            }
            else
            {
                var cues = SubtitleParser.Parse(SubtitleParser.Decode(File.ReadAllBytes(candidate.Path)), extension);
                analysis = analyzer.Analyze(cues);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            error = $"Could not read the file: {ex.Message}";
        }
        lock (session)
            session.Results[candidate.Path] = new SyncResult(info.Length, info.LastWriteTimeUtc, audioIndex, analysis, error);
    }

    private async Task<(IReadOnlyList<AudioTrack> Tracks, double Duration)> ProbeAsync(FfmpegTools tools, string path, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}";
        if (_probes.TryGetValue(key, out var cached))
            return cached;
        var output = await FfmpegRunner.RunAsync(tools.Ffprobe,
            ["-v", "error", "-print_format", "json", "-show_format", "-show_streams", path], ct);
        if (output.ExitCode != 0)
            throw new InvalidOperationException(output.LastError.Length > 0 ? output.LastError : "ffprobe could not read this video");
        var duration = VideoInfo.Parse(output.StdOut).DurationSeconds;
        var probed = (AudioTrack.Parse(output.StdOut), duration);
        if (_probes.Count > 50)
            _probes.Clear();
        return _probes[key] = probed;
    }

    private async Task<float[]> SpeechAsync(
        FfmpegTools tools, string path, int audioIndex, double duration, Action<double, string> progress, CancellationToken ct)
    {
        var info = new FileInfo(path);
        var key = $"{path}|{info.Length}|{info.LastWriteTimeUtc.Ticks}|{audioIndex}";
        if (_speech.TryGetValue(key, out var cached))
            return cached;

        progress(2, "Listening to the audio…");
        var loudness = await AudioExtractor.LoudnessAsync(tools.Ffmpeg, path, audioIndex, duration,
            new Progress<double>(p => progress(2 + p * 80, "Listening to the audio…")), ct);
        progress(83, "Finding where people speak…");
        var speech = SpeechDetector.Detect(loudness);
        if (_speech.Count >= 12)
            _speech.Clear();
        return _speech[key] = speech;
    }

    // ---- Applying a fix ------------------------------------------------------------------------

    public sealed record ApplyOutcome(string Path, string Message);

    /// <summary>
    /// Writes the subtitle as it should be: next to the video under its name, corrected when the analysis
    /// found a fix. A file that was there is kept as <c>name.srt.bak</c> (the first time only, so the true
    /// original survives repeated fixes).
    /// </summary>
    public ApplyOutcome Apply(string itemId, string path)
    {
        path = Normalize(path);
        var item = _library.Get(itemId) ?? throw new InvalidOperationException("This video is not in the list any more");
        var session = SessionFor(itemId);
        SyncAnalysis analysis;
        lock (session)
        {
            if (session.Results.GetValueOrDefault(path) is not { Analysis: { } found } result || !Current(result, path))
                throw new InvalidOperationException("This subtitle changed since it was compared — wait for the new comparison");
            analysis = found;
        }
        if (analysis.Verdict is not (SyncVerdict.InSync or SyncVerdict.Shift or SyncVerdict.Rescale))
            throw new InvalidOperationException("There is no safe fix for this subtitle");

        var candidate = Candidates(item, session).FirstOrDefault(c => string.Equals(c.Path, path, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException("This subtitle file is not there any more");
        var extension = Path.GetExtension(path);
        var target = TargetFor(item, candidate);
        if (candidate.Companion && analysis.Fix is null)
            throw new InvalidOperationException("This subtitle is already in sync");

        var original = File.ReadAllBytes(path);
        byte[] written;
        if (analysis.Fix is { } fix)
        {
            var hadBom = original.Length >= 3 && original[0] == 0xEF && original[1] == 0xBB && original[2] == 0xBF;
            var moved = SubtitleRetimer.Retime(SubtitleParser.Decode(original), extension, fix.Scale, fix.Offset);
            written = [.. new UTF8Encoding(hadBom).GetPreamble(), .. new UTF8Encoding(false).GetBytes(moved)];
        }
        else
        {
            written = original; // in sync, only needed under the video's name
        }

        string? backup = null;
        if (File.Exists(target))
        {
            backup = target + ".bak";
            if (!File.Exists(backup))
                File.Copy(target, backup);
        }
        var temp = target + ".tmp";
        File.WriteAllBytes(temp, written);
        File.Move(temp, target, overwrite: true);

        lock (session)
        {
            session.Applied[target] = new AppliedFix(target, backup, candidate.Companion ? null : path);
            if (!candidate.Companion)
                session.Extras.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
            session.Results.Remove(path);
        }
        _library.RefreshSubtitleStatus(_settings.Current.Languages);
        _events.Publish("library-changed");
        Start(itemId);
        return new ApplyOutcome(target, analysis.Fix is null
            ? $"Saved as {Path.GetFileName(target)}"
            : $"Fixed {Path.GetFileName(target)}" + (backup is null ? "" : " (the original is kept as .bak)"));
    }

    /// <summary>Puts the original back: from the backup next to the file, or by removing a file this app created.</summary>
    public ApplyOutcome Undo(string itemId, string path)
    {
        path = Normalize(path);
        var item = _library.Get(itemId) ?? throw new InvalidOperationException("This video is not in the list any more");
        var session = SessionFor(itemId);
        AppliedFix? applied;
        lock (session)
            session.Applied.TryGetValue(path, out applied);

        var backup = applied?.Backup ?? (File.Exists(path + ".bak") ? path + ".bak" : null);
        string message;
        if (backup is not null && File.Exists(backup))
        {
            File.Copy(backup, path, overwrite: true);
            File.Delete(backup);
            message = $"Restored the original {Path.GetFileName(path)}";
        }
        else if (applied is not null)
        {
            File.Delete(path);
            if (applied.Source is not null && File.Exists(applied.Source))
                lock (session)
                    session.Extras.Add(applied.Source);
            message = $"Removed {Path.GetFileName(path)}";
        }
        else
        {
            throw new InvalidOperationException("There is nothing to undo");
        }

        lock (session)
        {
            session.Applied.Remove(path);
            session.Results.Remove(path);
        }
        _library.RefreshSubtitleStatus(_settings.Current.Languages);
        _events.Publish("library-changed");
        Start(item.Id);
        return new ApplyOutcome(path, message);
    }
}
