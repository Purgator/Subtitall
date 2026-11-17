using System.Globalization;
using System.Text.Json;

namespace Subtitall.Core.Reencode;

/// <summary>What ffprobe says about a video file — only what the re-encoder needs.</summary>
public sealed record VideoInfo
{
    /// <summary>Codec of the main video track ("h264", "hevc", "av1"…); empty when there is none.</summary>
    public string VideoCodec { get; init; } = "";

    /// <summary>Index of that track among the file's tracks.</summary>
    public int VideoStreamIndex { get; init; }

    public int Width { get; init; }

    public int Height { get; init; }

    public string PixelFormat { get; init; } = "";

    public double Fps { get; init; }

    public double DurationSeconds { get; init; }

    public long SizeBytes { get; init; }

    /// <summary>Overall bitrate of the file, in bits per second.</summary>
    public long BitRate { get; init; }

    /// <summary>HDR10 / HLG / Dolby Vision material: its colours need care a plain re-encode doesn't give.</summary>
    public bool Hdr { get; init; }

    public int AudioTracks { get; init; }

    public int SubtitleTracks { get; init; }

    public bool HasVideo => VideoCodec.Length > 0 && Width > 0 && Height > 0;

    /// <summary>Reads the output of <c>ffprobe -print_format json -show_format -show_streams</c>.</summary>
    public static VideoInfo Parse(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        double duration = 0;
        long size = 0, bitRate = 0;
        if (root.TryGetProperty("format", out var format))
        {
            duration = Number(format, "duration");
            size = (long)Number(format, "size");
            bitRate = (long)Number(format, "bit_rate");
        }

        var info = new VideoInfo();
        int audio = 0, subtitles = 0;
        JsonElement? video = null;
        if (root.TryGetProperty("streams", out var streams) && streams.ValueKind == JsonValueKind.Array)
        {
            foreach (var stream in streams.EnumerateArray())
            {
                switch (Text(stream, "codec_type"))
                {
                    case "audio": audio++; break;
                    case "subtitle": subtitles++; break;
                    case "video" when video is null && !IsCoverArt(stream): video = stream; break;
                }
            }
        }

        if (video is { } v)
        {
            if (duration <= 0)
                duration = Number(v, "duration");
            var transfer = Text(v, "color_transfer");
            info = info with
            {
                VideoCodec = Text(v, "codec_name"),
                VideoStreamIndex = (int)Number(v, "index"),
                Width = (int)Number(v, "width"),
                Height = (int)Number(v, "height"),
                PixelFormat = Text(v, "pix_fmt"),
                Fps = Rate(Text(v, "avg_frame_rate")) is > 0 and var avg ? avg : Rate(Text(v, "r_frame_rate")),
                Hdr = transfer is "smpte2084" or "arib-std-b67" || HasDolbyVision(v),
            };
        }

        if (bitRate <= 0 && duration > 0 && size > 0)
            bitRate = (long)(size * 8 / duration);

        return info with
        {
            DurationSeconds = duration,
            SizeBytes = size,
            BitRate = bitRate,
            AudioTracks = audio,
            SubtitleTracks = subtitles,
        };
    }

    // Cover images are stored as video tracks flagged "attached picture".
    private static bool IsCoverArt(JsonElement stream) =>
        stream.TryGetProperty("disposition", out var d) && Number(d, "attached_pic") > 0;

    private static bool HasDolbyVision(JsonElement stream)
    {
        if (!stream.TryGetProperty("side_data_list", out var list) || list.ValueKind != JsonValueKind.Array)
            return false;
        foreach (var entry in list.EnumerateArray())
            if (Text(entry, "side_data_type").Contains("DOVI", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string Text(JsonElement element, string name) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    // ffprobe writes most numbers as strings ("duration": "1325.4"), a few as real numbers.
    private static double Number(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            return 0;
        return value.ValueKind switch
        {
            JsonValueKind.Number => value.GetDouble(),
            JsonValueKind.String when double.TryParse(value.GetString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var n) => n,
            _ => 0,
        };
    }

    // "24000/1001" → 23.976
    private static double Rate(string text)
    {
        var parts = text.Split('/');
        if (parts.Length == 2
            && double.TryParse(parts[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var num)
            && double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var den)
            && den > 0)
            return num / den;
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var plain) ? plain : 0;
    }
}
