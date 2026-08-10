using System.Text.Json;

namespace Subtitall.Core;

/// <summary>Loads and saves <see cref="AppSettings"/> to %APPDATA%/Subtitall/settings.json.</summary>
public sealed class SettingsService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNameCaseInsensitive = true,
    };

    private readonly string _filePath;
    private readonly Lock _lock = new();
    private AppSettings _current;

    public SettingsService()
        : this(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Subtitall"))
    {
    }

    public SettingsService(string directory)
    {
        Directory.CreateDirectory(directory);
        _filePath = Path.Combine(directory, "settings.json");
        _current = Load();
    }

    public AppSettings Current
    {
        get { lock (_lock) return _current; }
    }

    public void Save(AppSettings settings)
    {
        lock (_lock)
        {
            _current = Normalize(settings);
            File.WriteAllText(_filePath, JsonSerializer.Serialize(_current, JsonOptions));
        }
    }

    private AppSettings Load()
    {
        try
        {
            if (File.Exists(_filePath))
            {
                var loaded = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(_filePath), JsonOptions);
                if (loaded is not null)
                    return Normalize(loaded);
            }
        }
        catch (Exception)
        {
            // Corrupt settings file — fall back to defaults rather than crash.
        }
        return new AppSettings();
    }

    private static AppSettings Normalize(AppSettings settings)
    {
        settings.Languages = settings.Languages
            .Select(l => l.Trim().ToLowerInvariant())
            .Where(l => l.Length > 0)
            .Distinct()
            .ToList();
        if (settings.Languages.Count == 0)
            settings.Languages = ["en"];
        return settings;
    }
}
