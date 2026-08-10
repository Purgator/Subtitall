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

static async Task OnElectronAppReady()
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
}
