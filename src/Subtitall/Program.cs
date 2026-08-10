using ElectronNET.API;
using ElectronNET.API.Entities;
using Subtitall.Api;
using Subtitall.Core;
using Subtitall.Core.Providers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
builder.Services.AddHttpClient<TvMazeService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<LibraryService>();
builder.Services.AddSingleton<ISubtitleProvider, OpenSubtitlesProvider>();
builder.Services.AddSingleton<ISubtitleProvider, SubDlProvider>();
builder.Services.AddSingleton<ISubtitleProvider, GestdownProvider>();
builder.Services.AddSingleton<SubtitleSearchService>();
builder.Services.AddSingleton<RenameService>();
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<BatchSearchService>();

builder.UseElectron(args, OnElectronAppReady);

var app = builder.Build();

app.UseDefaultFiles();
app.UseStaticFiles();
app.MapSubtitallApi();

app.Run();

static async Task OnElectronAppReady(IServiceProvider services)
{
    var options = new BrowserWindowOptions
    {
        Width = 1280,
        Height = 840,
        MinWidth = 960,
        MinHeight = 600,
        Show = false,
        BackgroundColor = "#1e1e1e",
        Title = "Subtitall",
    };
    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        options.AutoHideMenuBar = true;

    var window = await Electron.WindowManager.CreateWindowAsync(options);

    window.OnReadyToShow += () => window.Show();
    window.OnClosed += () => Electron.App.Quit();

    await CheckForUpdatesAsync(services);
}

// Auto-update via GitHub Releases (electron-updater). In unpackaged dev runs the
// updater no-ops by design, so this is safe to call unconditionally.
static async Task CheckForUpdatesAsync(IServiceProvider services)
{
    var logger = services.GetRequiredService<ILogger<Program>>();
    var events = services.GetRequiredService<EventBus>();
    try
    {
        Electron.AutoUpdater.OnError += message =>
            logger.LogWarning("Auto-updater: {Message}", message);
        Electron.AutoUpdater.OnUpdateDownloaded += info =>
        {
            logger.LogInformation("Update {Version} downloaded", info.Version);
            events.Publish("update-downloaded", new { version = info.Version });
        };
        // Downloads in the background and installs when the app quits
        // (autoInstallOnAppQuit is electron-updater's default).
        await Electron.AutoUpdater.CheckForUpdatesAndNotifyAsync();
    }
    catch (Exception ex)
    {
        // Never let update plumbing break app startup (e.g. offline).
        logger.LogWarning(ex, "Update check failed");
    }
}

public partial class Program;
