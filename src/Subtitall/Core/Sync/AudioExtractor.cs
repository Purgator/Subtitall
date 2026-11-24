using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Subtitall.Core.Sync;

/// <summary>One audio track of a video, as ffprobe lists them. <see cref="Index"/> counts audio tracks only (0 = the first).</summary>
public sealed record AudioTrack(int Index, string Language, string Title, string Codec, int Channels, bool Default)
{
    /// <summary>"eng · AC3 5.1 · Commentary".</summary>
    public string Label
    {
        get
        {
            var parts = new List<string> { Language.Length > 0 ? Language : $"Track {Index + 1}" };
            if (Codec.Length > 0)
                parts.Add(Codec.ToUpperInvariant() + (Channels > 0 ? " " + ChannelLayout(Channels) : ""));
            if (Title.Length > 0)
                parts.Add(Title);
            return string.Join(" · ", parts);
        }
    }

    private static string ChannelLayout(int channels) => channels switch
    {
        1 => "mono",
        2 => "stereo",
        6 => "5.1",
        8 => "7.1",
        _ => channels + " ch",
    };

    /// <summary>The audio tracks in the output of <c>ffprobe -print_format json -show_streams</c>.</summary>
    public static IReadOnlyList<AudioTrack> Parse(string json)
    {
        var tracks = new List<AudioTrack>();
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("streams", out var streams) || streams.ValueKind != JsonValueKind.Array)
            return tracks;
        foreach (var stream in streams.EnumerateArray())
        {
            if (Text(stream, "codec_type") != "audio")
                continue;
            var tags = stream.TryGetProperty("tags", out var t) ? t : default;
            var isDefault = stream.TryGetProperty("disposition", out var d) && d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("default", out var flag) && flag.ValueKind == JsonValueKind.Number && flag.GetInt32() > 0;
            tracks.Add(new AudioTrack(
                tracks.Count,
                Text(tags, "language") is { Length: > 0 } language && language != "und" ? language : "",
                Text(tags, "title"),
                Text(stream, "codec_name"),
                stream.TryGetProperty("channels", out var c) && c.ValueKind == JsonValueKind.Number ? c.GetInt32() : 0,
                isDefault));
        }
        return tracks;
    }

    /// <summary>The track to listen to unless the user picks another: the one flagged default, else the first.</summary>
    public static int Preferred(IReadOnlyList<AudioTrack> tracks) =>
        tracks.FirstOrDefault(t => t.Default)?.Index ?? 0;

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}

/// <summary>Has ffmpeg decode one audio track to speech-band mono PCM and measures it as it streams out.</summary>
public static class AudioExtractor
{
    /// <summary>The arguments that make ffmpeg write mono 8 kHz PCM of the speech band (150 Hz – 3.5 kHz) on stdout.</summary>
    public static IReadOnlyList<string> Arguments(string videoPath, int audioIndex) =>
    [
        "-v", "error", "-nostdin",
        "-i", videoPath,
        "-map", $"0:a:{audioIndex}",
        "-vn", "-sn", "-dn",
        "-ac", "1", "-ar", FrameEnergy.SampleRate.ToString(CultureInfo.InvariantCulture),
        "-af", "highpass=f=150,lowpass=f=3500",
        "-f", "s16le", "pipe:1",
    ];

    /// <summary>
    /// The loudness of every 20 ms of the track. <paramref name="progress"/> gets 0–1 when the
    /// length of the video is known.
    /// </summary>
    public static async Task<float[]> LoudnessAsync(
        string ffmpeg, string videoPath, int audioIndex, double durationSeconds,
        IProgress<double>? progress, CancellationToken ct)
    {
        var start = new ProcessStartInfo(ffmpeg)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in Arguments(videoPath, audioIndex))
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        var errors = new StringBuilder();
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (errors)
            {
                if (errors.Length > 16_000) errors.Remove(0, 8_000);
                errors.AppendLine(e.Data);
            }
        };
        process.Start();
        // A long decode must not make the computer sluggish.
        try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { /* not allowed — fine */ }
        process.BeginErrorReadLine();

        var energy = new FrameEnergy();
        try
        {
            var buffer = new byte[256 * 1024];
            var stream = process.StandardOutput.BaseStream;
            var lastReport = 0;
            int read;
            while ((read = await stream.ReadAsync(buffer, ct)) > 0)
            {
                energy.Add(buffer.AsSpan(0, read));
                if (progress is not null && durationSeconds > 0 && energy.Frames - lastReport >= 5000)
                {
                    lastReport = energy.Frames;
                    progress.Report(Math.Min(1, energy.Frames * FrameEnergy.FrameSeconds / durationSeconds));
                }
            }
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            throw;
        }
        process.WaitForExit(); // lets the last error lines arrive

        if (process.ExitCode != 0 && energy.Frames == 0)
        {
            string last;
            lock (errors)
                last = errors.ToString().Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
            throw new InvalidOperationException(last.Length > 0 ? last : "ffmpeg could not read the audio");
        }
        progress?.Report(1);
        return energy.ToArray();
    }
}
