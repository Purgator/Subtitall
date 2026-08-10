namespace Subtitall.Core;

/// <summary>User settings, persisted as JSON in %APPDATA%/Subtitall.</summary>
public sealed class AppSettings
{
    /// <summary>Subtitle languages to fetch, as ISO 639-1 codes, in priority order.</summary>
    public List<string> Languages { get; set; } = ["en"];

    public ProviderSettings OpenSubtitles { get; set; } = new();

    public ProviderSettings SubDl { get; set; } = new();

    public ProviderSettings Podnapisi { get; set; } = new() { Enabled = true };

    /// <summary>Template used by the rename feature.</summary>
    public string RenameTemplate { get; set; } = "{Show} - S{Season}E{Episode} - {Title}";

    /// <summary>Re-download and overwrite subtitle files that already exist.</summary>
    public bool OverwriteExisting { get; set; }
}

public sealed class ProviderSettings
{
    public bool Enabled { get; set; }

    public string ApiKey { get; set; } = "";
}
