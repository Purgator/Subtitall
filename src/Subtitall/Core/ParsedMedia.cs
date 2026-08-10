namespace Subtitall.Core;

/// <summary>Information extracted from a video filename.</summary>
public sealed record ParsedMedia
{
    /// <summary>Cleaned show or movie title.</summary>
    public required string Title { get; init; }

    public int? Season { get; init; }

    public int? Episode { get; init; }

    /// <summary>Last episode for multi-episode files (e.g. S01E01E02).</summary>
    public int? EndEpisode { get; init; }

    /// <summary>Absolute episode number (anime-style numbering, no season).</summary>
    public int? AbsoluteEpisode { get; init; }

    public int? Year { get; init; }

    public string? ReleaseGroup { get; init; }

    public bool IsEpisode => (Season.HasValue && Episode.HasValue) || AbsoluteEpisode.HasValue;
}
