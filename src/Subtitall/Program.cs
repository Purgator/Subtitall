using ElectronNET.API;
using ElectronNET.API.Entities;
using Subtitall.Api;
using Subtitall.Core;
using Subtitall.Core.Providers;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddHttpClient();
// Enums go out as camelCase strings ("quota", not 6) so the UI can read them.
builder.Services.ConfigureHttpJsonOptions(o =>
    o.SerializerOptions.Converters.Add(new System.Text.Json.Serialization.JsonStringEnumConverter(System.Text.Json.JsonNamingPolicy.CamelCase)));
builder.Services.AddSingleton<TvMazeService>();
builder.Services.AddSingleton<TmdbService>();
builder.Services.AddSingleton<ShowMetadataService>();
builder.Services.AddSingleton<ShowDetectionService>();
builder.Services.AddSingleton<MediaDetectionService>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<LibraryService>();
builder.Services.AddSingleton<ProviderHealth>();
builder.Services.AddSingleton<ISubtitleProvider, OpenSubtitlesProvider>();
builder.Services.AddSingleton<ISubtitleProvider, SubDlProvider>();
builder.Services.AddSingleton<ISubtitleProvider, GestdownProvider>();
builder.Services.AddSingleton<ISubtitleProvider, SubSourceProvider>();
builder.Services.AddSingleton<SubtitleSearchService>();
builder.Services.AddSingleton<RenameService>();
builder.Services.AddSingleton<EventBus>();
builder.Services.AddSingleton<BatchSearchService>();
builder.Services.AddSingleton<UpdateService>();

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
        BackgroundColor = "#14161a",
        Title = "Subtitall",
    };
    if (OperatingSystem.IsWindows() || OperatingSystem.IsLinux())
        options.AutoHideMenuBar = true;

    var window = await Electron.WindowManager.CreateWindowAsync(options);

    window.OnReadyToShow += () => window.Show();
    window.OnClosed += () => Electron.App.Quit();

    // Auto-update via GitHub Releases (electron-updater), with its progress relayed to the
    // UI. In unpackaged dev runs the updater no-ops by design, so this is safe to call.
    await services.GetRequiredService<UpdateService>().CheckAsync();
}

public partial class Program;
