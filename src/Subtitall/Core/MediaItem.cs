namespace Subtitall.Core;

/// <summary>A video file linked into the app.</summary>
public sealed class MediaItem
{
    public required string Id { get; init; }

    public required string Path { get; set; }

    public string FileName => System.IO.Path.GetFileName(Path);

    public required ParsedMedia Parsed { get; set; }

    public long SizeBytes { get; init; }

    /// <summary>Language code → subtitle file already present next to the video.</summary>
    public Dictionary<string, bool> Subtitles { get; set; } = [];
}
