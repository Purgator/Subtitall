namespace Subtitall.Core;

/// <summary>A video file linked into the app.</summary>
public sealed class MediaItem
{
    public required string Id { get; init; }

    public required string Path { get; set; }

    public string FileName => System.IO.Path.GetFileName(Path);

    /// <summary>What the filename alone says.</summary>
    public required ParsedMedia Parsed { get; set; }

    public long SizeBytes { get; init; }

    /// <summary>Language code → subtitle file already present next to the video.</summary>
    public Dictionary<string, bool> Subtitles { get; set; } = [];

    /// <summary>Which show this is, per the metadata sources (null = not looked up yet).</summary>
    public ShowDetection? Detection { get; set; }

    /// <summary>Season/episode after guessing (null = not an episode, or nothing to go on yet).</summary>
    public EpisodeGuess? Guess { get; set; }

    /// <summary>Season the user assigned to files whose name carries only an episode number.</summary>
    public int? SeasonOverride { get; set; }

    /// <summary>
    /// What providers and the rename engine work with: the parsed name, with the season and
    /// episode filled in by the guesser when the filename didn't spell them out.
    /// </summary>
    public ParsedMedia Effective => Guess is { } g
        ? Parsed with { Season = g.Season, Episode = g.Episode, EndEpisode = g.EndEpisode }
        : Parsed;

    /// <summary>Other names the show is known under (the detected show's name and its akas).</summary>
    public IReadOnlyList<string> AlternateTitles
    {
        get
        {
            if (Detection?.Show is not { } show)
                return [];
            return new[] { show.Show.Name }.Concat(show.Aliases)
                .Where(n => !string.Equals(n, Parsed.Title, StringComparison.OrdinalIgnoreCase))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }
}
