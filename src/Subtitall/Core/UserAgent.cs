using System.Reflection;

namespace Subtitall.Core;

/// <summary>App identity sent to subtitle providers ("Subtitall v1.2.3" — OpenSubtitles requires this format).</summary>
public static class UserAgent
{
    public static string Version { get; } =
        Assembly.GetExecutingAssembly().GetName().Version is { } v
            ? $"{v.Major}.{v.Minor}.{v.Build}"
            : "0.0.0";

    public static string Value { get; } = $"Subtitall v{Version}";
}
