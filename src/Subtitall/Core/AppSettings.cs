namespace Subtitall.Core;

/// <summary>User settings, persisted as JSON in %APPDATA%/Subtitall.</summary>
public sealed class AppSettings
{
    /// <summary>Subtitle languages to fetch, as ISO 639-1 codes, in priority order.</summary>
    public List<string> Languages { get; set; } = ["en"];

    /// <summary>Enabled out of the box when the build ships a built-in key.</summary>
    public ProviderSettings OpenSubtitles { get; set; } = new() { Enabled = BuiltInKeys.OpenSubtitles.Length > 0 };

    public ProviderSettings SubDl { get; set; } = new();

    /// <summary>SubSource (former Subscene archive) — free API key.</summary>
    public ProviderSettings SubSource { get; set; } = new();

    /// <summary>TMDB — optional metadata source for renaming (free API key); TVMaze stays keyless.</summary>
    public ProviderSettings Tmdb { get; set; } = new() { Enabled = BuiltInKeys.Tmdb.Length > 0 };

    /// <summary>Gestdown (Addic7ed proxy) — keyless, TV shows only.</summary>
    public ProviderSettings Gestdown { get; set; } = new() { Enabled = true };

    /// <summary>Template used by the rename feature.</summary>
    public string RenameTemplate { get; set; } = "{Show} - S{Season}E{Episode} - {Title}";

    /// <summary>Re-download and overwrite subtitle files that already exist.</summary>
    public bool OverwriteExisting { get; set; }
}

public sealed class ProviderSettings
{
    public bool Enabled { get; set; }

    public string ApiKey { get; set; } = "";

    /// <summary>Optional account login — OpenSubtitles uses it for the per-user download quota.</summary>
    public string Username { get; set; } = "";

    public string Password { get; set; } = "";

    /// <summary>The user's own key when entered, else the key baked into the official build.</summary>
    public string ResolveApiKey(string builtIn) =>
        ApiKey.Trim() is { Length: > 0 } own ? own : builtIn;
}
