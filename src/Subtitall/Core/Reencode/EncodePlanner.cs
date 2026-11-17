using System.Globalization;

namespace Subtitall.Core.Reencode;

public enum TargetCodec { Hevc, Av1, H264 }

/// <summary>How much quality is traded for size. All three aim at "you can't tell at normal viewing".</summary>
public enum QualityLevel { High, Balanced, Compact }

public enum EncodeSpeed { Fast, Normal, Slow }

public sealed record EncodeOptions(TargetCodec Codec, QualityLevel Quality, EncodeSpeed Speed, int MaxHeight)
{
    public static EncodeOptions From(ReencodeSettings settings) =>
        new(settings.Codec, settings.Quality, settings.Speed, settings.MaxHeight is 480 or 720 or 1080 or 2160 ? settings.MaxHeight : 0);
}

public enum AdviceKind { Recommended, AlreadyEfficient, AlreadySmall, Hdr, NoVideo }

/// <summary>Whether re-encoding a file is worth it, and why not when it isn't.</summary>
public sealed record EncodeAdvice(AdviceKind Kind, string Reason)
{
    public bool Recommended => Kind == AdviceKind.Recommended;

    /// <summary>The user may still insist, except when there is no video to encode.</summary>
    public bool Possible => Kind != AdviceKind.NoVideo;
}

/// <summary>
/// Decides how a file is re-encoded: whether it is worth it, the ffmpeg arguments, where the new
/// file goes, and how a trial on short excerpts translates into an estimate for the whole file.
/// Pure — nothing here touches the disk or runs a process.
/// </summary>
public static class EncodePlanner
{
    private static readonly string[] EfficientCodecs = ["hevc", "av1", "vp9", "vvc"];

    public static EncodeAdvice Advise(VideoInfo info, EncodeOptions options)
    {
        if (!info.HasVideo)
            return new(AdviceKind.NoVideo, "No video track found");
        if (info.Hdr)
            return new(AdviceKind.Hdr, "HDR video — re-encoding could alter its colours");

        // Shrinking the picture always saves space, whatever the file is encoded with.
        if (TargetHeight(info, options) is not null)
            return new(AdviceKind.Recommended, "");

        if (EfficientCodecs.Contains(info.VideoCodec))
            return new(AdviceKind.AlreadyEfficient, $"Already {CodecName(info.VideoCodec)} — little to gain, some quality to lose");
        // About 0.7 bit per pixel per second: below that, a file is already tightly compressed.
        if (info.BitRate > 0 && info.BitRate < (long)info.Width * info.Height * 7 / 10)
            return new(AdviceKind.AlreadySmall, "Already small for its resolution — little to gain");
        return new(AdviceKind.Recommended, "");
    }

    public static string CodecName(string codec) => codec switch
    {
        "hevc" => "H.265",
        "h264" => "H.264",
        "av1" => "AV1",
        "vp9" => "VP9",
        "vvc" => "H.266",
        "mpeg4" => "MPEG-4",
        "mpeg2video" => "MPEG-2",
        "msmpeg4v3" => "DivX 3",
        "wmv3" or "vc1" => "WMV",
        "" => "?",
        _ => codec.ToUpperInvariant(),
    };

    /// <summary>The ffmpeg encoder a codec choice stands for.</summary>
    public static string Encoder(TargetCodec codec) => codec switch
    {
        TargetCodec.Av1 => "libsvtav1",
        TargetCodec.H264 => "libx264",
        _ => "libx265",
    };

    /// <summary>Constant-quality factor: lower = closer to the original, bigger file.</summary>
    public static int Crf(TargetCodec codec, QualityLevel quality) => (codec, quality) switch
    {
        (TargetCodec.Hevc, QualityLevel.High) => 19,
        (TargetCodec.Hevc, QualityLevel.Balanced) => 22,
        (TargetCodec.Hevc, _) => 25,
        (TargetCodec.Av1, QualityLevel.High) => 24,
        (TargetCodec.Av1, QualityLevel.Balanced) => 29,
        (TargetCodec.Av1, _) => 34,
        (_, QualityLevel.High) => 18,
        (_, QualityLevel.Balanced) => 21,
        _ => 24,
    };

    private static string Preset(TargetCodec codec, EncodeSpeed speed) => (codec, speed) switch
    {
        (TargetCodec.Av1, EncodeSpeed.Fast) => "8",
        (TargetCodec.Av1, EncodeSpeed.Normal) => "6",
        (TargetCodec.Av1, _) => "4",
        (_, EncodeSpeed.Fast) => "fast",
        (_, EncodeSpeed.Normal) => "medium",
        _ => "slow",
    };

    /// <summary>The height to scale down to, or null when the picture keeps its size.</summary>
    public static int? TargetHeight(VideoInfo info, EncodeOptions options) =>
        options.MaxHeight > 0 && info.Height > options.MaxHeight ? options.MaxHeight : null;

    /// <summary>Encoder, quality and picture arguments — shared by the trial and the real encode.</summary>
    public static List<string> VideoArgs(VideoInfo info, EncodeOptions options)
    {
        var args = new List<string>();
        if (TargetHeight(info, options) is { } height)
            args.AddRange(["-vf", $"scale=-2:{height}:flags=lanczos"]);

        args.AddRange(["-c:v", Encoder(options.Codec), "-preset", Preset(options.Codec, options.Speed),
            "-crf", Crf(options.Codec, options.Quality).ToString(CultureInfo.InvariantCulture)]);

        var deep = info.PixelFormat.Contains("10") || info.PixelFormat.Contains("12");
        switch (options.Codec)
        {
            case TargetCodec.Hevc:
                args.AddRange(["-x265-params", "log-level=error"]);
                if (!IsPlanarYuv(info.PixelFormat))
                    args.AddRange(["-pix_fmt", "yuv420p"]);
                break;
            case TargetCodec.Av1:
                // SVT-AV1 only takes 4:2:0.
                args.AddRange(["-pix_fmt", deep ? "yuv420p10le" : "yuv420p"]);
                break;
            default:
                if (!IsPlanarYuv(info.PixelFormat))
                    args.AddRange(["-pix_fmt", "yuv420p"]);
                break;
        }
        return args;
    }

    // The formats x264/x265 take as they are; anything else (RGB, palettes…) is converted.
    private static bool IsPlanarYuv(string pixelFormat) =>
        pixelFormat.StartsWith("yuv4", StringComparison.Ordinal) || pixelFormat.StartsWith("yuvj4", StringComparison.Ordinal);

    /// <summary>
    /// Arguments of a full re-encode: the video is encoded, everything else (audio, subtitles,
    /// fonts, chapters, tags) is copied untouched.
    /// </summary>
    public static List<string> BuildEncodeArgs(VideoInfo info, EncodeOptions options, string input, string output)
    {
        var mp4 = OutputExtension(input) == ".mp4";
        var args = new List<string> { "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-nostats", "-progress", "pipe:1" };
        // Old containers (AVI…) often carry no timestamps; Matroska needs them.
        if (!mp4 && !IsMatroska(input))
            args.AddRange(["-fflags", "+genpts"]);
        args.AddRange(["-i", input, "-map", "0:V", "-map", "0:a?", "-map", "0:s?"]);
        if (!mp4)
            args.AddRange(["-map", "0:t?"]); // fonts used by styled subtitles
        args.AddRange(["-c", "copy"]);
        args.AddRange(VideoArgs(info, options));
        args.AddRange(["-max_muxing_queue_size", "4096"]);
        if (mp4)
        {
            args.AddRange(["-movflags", "+faststart"]);
            if (options.Codec == TargetCodec.Hevc)
                args.AddRange(["-tag:v", "hvc1"]); // what Apple players expect
        }
        args.AddRange(["-f", mp4 ? "mp4" : "matroska", output]);
        return args;
    }

    /// <summary>Arguments encoding one short excerpt, video only — for the trial.</summary>
    public static List<string> BuildSampleArgs(VideoInfo info, EncodeOptions options, string input, double start, double length, string output)
    {
        var args = new List<string>
        {
            "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-nostats",
            "-ss", Seconds(start), "-t", Seconds(length), "-i", input,
            "-map", "0:V:0", "-an", "-sn", "-dn",
        };
        args.AddRange(VideoArgs(info, options));
        args.AddRange(["-f", "matroska", output]);
        return args;
    }

    public static string Seconds(double value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static bool IsMatroska(string path) =>
        Path.GetExtension(path).ToLowerInvariant() is ".mkv" or ".webm";

    /// <summary>MP4 files stay MP4; everything else becomes Matroska, which can hold any track.</summary>
    public static string OutputExtension(string sourcePath) =>
        Path.GetExtension(sourcePath).ToLowerInvariant() is ".mp4" or ".m4v" ? ".mp4" : ".mkv";

    public const string PendingMarker = ".reencoded";

    /// <summary>Where the new file waits, next to the original, until the user keeps or discards it.</summary>
    public static string PendingPath(string sourcePath) =>
        Path.Combine(Path.GetDirectoryName(sourcePath) ?? "", Path.GetFileNameWithoutExtension(sourcePath) + PendingMarker + OutputExtension(sourcePath));

    /// <summary>The name the new file takes once it replaces the original.</summary>
    public static string FinalPath(string sourcePath) =>
        Path.Combine(Path.GetDirectoryName(sourcePath) ?? "", Path.GetFileNameWithoutExtension(sourcePath) + OutputExtension(sourcePath));

    /// <summary>A re-encoded copy waiting for a decision is not a video of its own.</summary>
    public static bool IsPendingFile(string path) =>
        Path.GetFileNameWithoutExtension(path).EndsWith(PendingMarker, StringComparison.OrdinalIgnoreCase)
        || path.EndsWith(".part", StringComparison.OrdinalIgnoreCase);

    /// <summary>The excerpts a trial encodes: three short ones spread over the file, one for a short clip.</summary>
    public static (IReadOnlyList<double> Starts, double Length) SamplePositions(double durationSeconds)
    {
        if (durationSeconds <= 0)
            return ([0], 8);
        if (durationSeconds < 45)
            return ([0], Math.Min(durationSeconds, 12));
        return ([durationSeconds * 0.2, durationSeconds * 0.5, durationSeconds * 0.8], 8);
    }

    /// <summary>
    /// Size of the whole file once re-encoded, from a trial: <paramref name="videoShare"/> of the
    /// file is video, and the encoded excerpts weigh <paramref name="videoRatio"/> of the original ones.
    /// </summary>
    public static long EstimateSize(long fileBytes, double videoShare, double videoRatio)
    {
        videoShare = Math.Clamp(videoShare, 0, 1);
        return (long)(fileBytes * (1 - videoShare + videoShare * Math.Max(videoRatio, 0)));
    }

    /// <summary>Space saved, in percent of the original (negative when the new file is bigger).</summary>
    public static double GainPercent(long originalBytes, long newBytes) =>
        originalBytes <= 0 ? 0 : Math.Round((1 - (double)newBytes / originalBytes) * 100, 1);

    /// <summary>Plain words for an SSIM score (1 = identical pictures).</summary>
    public static string QualityLabel(double ssim) => ssim switch
    {
        >= 0.985 => "Visually identical",
        >= 0.97 => "Excellent",
        >= 0.95 => "Very good",
        >= 0.92 => "Good",
        _ => "Visible loss",
    };

    /// <summary>A new file can replace the original only if it runs as long.</summary>
    public static bool DurationMatches(double original, double encoded) =>
        original <= 0 || Math.Abs(original - encoded) <= Math.Max(2, original * 0.01);
}
