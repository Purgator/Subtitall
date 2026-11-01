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

    /// <summary>
    /// Episode number found without a season: anime absolute numbering ("- 59") or a bare
    /// in-season number ("The Final Season - 15"). <see cref="EpisodeGuesser"/> decides which.
    /// </summary>
    public int? AbsoluteEpisode { get; init; }

    /// <summary>Season named inside the title ("Season 3", "3rd Season", "S3"), if any.</summary>
    public int? SeasonHint { get; init; }

    /// <summary>The title said "The Final Season" — i.e. the show's last season.</summary>
    public bool FinalSeasonHint { get; init; }

    public int? Year { get; init; }

    public string? ReleaseGroup { get; init; }

    public bool IsEpisode => (Season.HasValue && Episode.HasValue) || AbsoluteEpisode.HasValue;
}
