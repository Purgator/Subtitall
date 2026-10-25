namespace Subtitall.Core.Providers;

/// <summary>A subtitle found on a provider, before download.</summary>
public sealed record SubtitleCandidate
{
    public required string Provider { get; init; }

    /// <summary>Provider-specific reference used to download this subtitle.</summary>
    public required string DownloadRef { get; init; }

    public required string Language { get; init; }

    public string ReleaseName { get; init; } = "";

    public int Downloads { get; init; }

    /// <summary>Exact file match via moviehash — the strongest possible signal.</summary>
    public bool HashMatch { get; init; }

    /// <summary>Subtitle for the deaf/hard-of-hearing (sound cues in brackets).</summary>
    public bool HearingImpaired { get; init; }

    /// <summary>Ranking score, filled in by the search orchestrator.</summary>
    public double Score { get; set; }
}

/// <summary>Downloaded subtitle payload (always a single subtitle file, e.g. .srt).</summary>
public sealed record SubtitlePayload(byte[] Content, string Extension);

public interface ISubtitleProvider
{
    string Name { get; }

    /// <summary>True when the provider is enabled and configured (API key present if needed).</summary>
    bool IsConfigured(AppSettings settings);

    /// <summary>Searches subtitles for one video in one language (ISO 639-1 code).</summary>
    Task<IReadOnlyList<SubtitleCandidate>> SearchAsync(
        MediaItem item, string language, AppSettings settings, CancellationToken ct);

    Task<SubtitlePayload> DownloadAsync(
        SubtitleCandidate candidate, AppSettings settings, CancellationToken ct);
}
