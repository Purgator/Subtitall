using System.Reflection;

namespace Subtitall.Core;

/// <summary>
/// API keys baked into the official build so the app works out of the box. They are injected
/// by CI as MSBuild properties (see release.yml) — never committed — and land here as assembly
/// metadata. Dev builds have none; a user-entered key always wins (ProviderSettings.ResolveApiKey).
/// </summary>
public static class BuiltInKeys
{
    public static string OpenSubtitles { get; } = Read("BuiltInOpenSubtitlesKey");

    public static string Tmdb { get; } = Read("BuiltInTmdbKey");

    private static string Read(string key) =>
        typeof(BuiltInKeys).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>()
            .FirstOrDefault(a => a.Key == key)?.Value?.Trim() ?? "";
}
