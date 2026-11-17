using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace Subtitall.Core.Reencode;

public sealed record ProcessOutput(int ExitCode, string StdOut, string StdErr)
{
    /// <summary>The last meaningful line ffmpeg wrote — its error messages end up there.</summary>
    public string LastError =>
        StdErr.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? "";
}

/// <summary>One step of an encode, as ffmpeg reports it with <c>-progress</c>.</summary>
public sealed record EncodeProgress(double Seconds, double Speed);

/// <summary>Runs ffmpeg / ffprobe and reads what they print.</summary>
public static partial class FfmpegRunner
{
    /// <summary>
    /// Runs a tool to its end. Cancelling kills it. <paramref name="onOutputLine"/> receives
    /// stdout line by line as it comes (progress reports); stdout is not kept then.
    /// </summary>
    public static async Task<ProcessOutput> RunAsync(
        string executable, IEnumerable<string> arguments, CancellationToken ct,
        Action<string>? onOutputLine = null, bool background = false)
    {
        var start = new ProcessStartInfo(executable)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = new Process { StartInfo = start };
        var stdout = new StringBuilder();
        var stderr = new StringBuilder();
        process.OutputDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            if (onOutputLine is not null) onOutputLine(e.Data);
            else lock (stdout) stdout.AppendLine(e.Data);
        };
        process.ErrorDataReceived += (_, e) =>
        {
            if (e.Data is null) return;
            lock (stderr)
            {
                // Keep the end only: a long encode can warn thousands of times.
                if (stderr.Length > 64_000) stderr.Remove(0, 32_000);
                stderr.AppendLine(e.Data);
            }
        };

        process.Start();
        if (background)
        {
            // A long encode must not make the computer sluggish.
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception) { /* not allowed — fine */ }
        }
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        try
        {
            await process.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (Exception) { /* already gone */ }
            try { await process.WaitForExitAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5), CancellationToken.None); }
            catch (Exception) { /* give up waiting */ }
            throw;
        }
        // Lets the last output lines arrive.
        process.WaitForExit();
        return new ProcessOutput(process.ExitCode, stdout.ToString(), stderr.ToString());
    }

    /// <summary>
    /// Folds the <c>key=value</c> lines of <c>-progress</c> into a progress report; returns one
    /// each time ffmpeg closes a block (its "progress=" line).
    /// </summary>
    public sealed class ProgressReader
    {
        private double _seconds;
        private double _speed;

        public EncodeProgress? Read(string line)
        {
            var split = line.IndexOf('=');
            if (split <= 0)
                return null;
            var key = line[..split].Trim();
            var value = line[(split + 1)..].Trim();
            switch (key)
            {
                // Both are microseconds, despite the name of the second one.
                case "out_time_us" or "out_time_ms":
                    if (long.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var us) && us >= 0)
                        _seconds = us / 1_000_000.0;
                    break;
                case "speed":
                    if (double.TryParse(value.TrimEnd('x'), NumberStyles.Float, CultureInfo.InvariantCulture, out var speed))
                        _speed = speed;
                    break;
                case "progress":
                    return new EncodeProgress(_seconds, _speed);
            }
            return null;
        }
    }

    /// <summary>The overall score in the summary line of ffmpeg's ssim filter, or null.</summary>
    public static double? ParseSsim(string stderr)
    {
        var match = SsimPattern().Matches(stderr).LastOrDefault();
        return match is not null && double.TryParse(match.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    /// <summary>"ffmpeg version 7.1-full_build-www.gyan.dev Copyright…" → "7.1".</summary>
    public static string ParseVersion(string versionOutput)
    {
        var match = VersionPattern().Match(versionOutput);
        if (!match.Success)
            return "";
        var raw = match.Groups[1].Value;
        var number = VersionNumberPattern().Match(raw);
        return number.Success ? number.Groups[1].Value : raw;
    }

    /// <summary>
    /// Bytes of the main video track and of all tracks among the packets ffprobe listed as
    /// <c>stream_index,pts_time,size</c> lines, keeping those inside [start, start + length).
    /// </summary>
    public static (long Video, long All) SumPackets(string csv, int videoStreamIndex, double start, double length)
    {
        long video = 0, all = 0;
        foreach (var line in csv.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var parts = line.Split(',');
            if (parts.Length < 3
                || !int.TryParse(parts[0], out var index)
                || !double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var time)
                || !long.TryParse(parts[2], out var size))
                continue;
            if (time < start || time >= start + length)
                continue;
            all += size;
            if (index == videoStreamIndex)
                video += size;
        }
        return (video, all);
    }

    [GeneratedRegex(@"SSIM .*All:([0-9.]+)")]
    private static partial Regex SsimPattern();

    [GeneratedRegex(@"^ff(?:mpeg|probe) version (\S+)", RegexOptions.Multiline)]
    private static partial Regex VersionPattern();

    [GeneratedRegex(@"^n?(\d+(?:\.\d+)+)")]
    private static partial Regex VersionNumberPattern();
}
