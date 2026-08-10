# Subtitall

[![Latest release](https://img.shields.io/github/v/release/Purgator/Subtitall)](https://github.com/Purgator/Subtitall/releases/latest)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

Find and download subtitles for your movies, TV shows and anime — a lightweight, VS Code-style desktop app with smart episode renaming.

Inspired by [Subber](https://github.com/noembryo/Subber).

## Features

- 🎬 **Link videos your way** — drag & drop files or folders anywhere in the window, or use the Add buttons. Folders are scanned recursively.
- 🧠 **Smart filename parsing** — show title, `S01E02` / `1x02` / anime absolute numbering, year and release group are detected automatically.
- 🌍 **Multiple languages** — pick any number of subtitle languages; each is saved as `video.<lang>.srt` next to the video so every media player picks it up.
- 🔎 **Three subtitle sources**
  - **Gestdown** (Addic7ed mirror) — TV shows, works out of the box, no account needed
  - **OpenSubtitles** — movies + TV, free API key
  - **SubDL** — movies + TV, free API key
- 🏆 **Best match wins** — results are ranked (exact file hash match, release-name similarity, popularity) and the best one downloads automatically. Prefer choosing yourself? Click a language chip to pick from the full list.
- ✏️ **Episode renaming** — match your files against [TVMaze](https://www.tvmaze.com), preview `Show - S01E02 - Title.mkv` names, confirm, done. Subtitles sitting next to the videos are renamed too.
- 🔄 **Auto-update** — the app updates itself from GitHub Releases.

## Install (Windows)

1. Download `Subtitall-Setup-<version>.exe` from the [latest release](https://github.com/Purgator/Subtitall/releases/latest).
2. Run it. Windows SmartScreen may warn because the app is not code-signed — choose *More info* → *Run anyway*.
3. Launch **Subtitall** from the Start menu. Updates install automatically when you close the app.

## Quick start

1. **Add videos** — drop files or a folder into the window.
2. *(Optional)* Open **Settings** (gear icon) to pick your languages and add provider API keys:
   - OpenSubtitles: create a free key under [API consumers](https://www.opensubtitles.com/en/consumers). Adding your username/password raises your daily download quota.
   - SubDL: grab a free key from your [SubDL panel](https://subdl.com/panel/api).
   - Gestdown needs nothing and is enabled by default (TV shows only).
3. Hit **Search subtitles**. Green chips = subtitle saved next to the video.
4. **Rename episodes** (pencil icon): search your show, check the proposed names, click *Rename*.

## Development

### Prerequisites

- [.NET SDK 10](https://dotnet.microsoft.com/download) (or newer)
- [Node.js 22+](https://nodejs.org) on your `PATH` (required by Electron.NET)

### Run

```bash
dotnet run --project src/Subtitall
```

This starts the ASP.NET Core backend and opens the Electron window ([ElectronNET.Core](https://github.com/ElectronNET/Electron.NET) launches Electron automatically).

### Test

```bash
dotnet test
```

### Package a Windows installer

```bash
dotnet publish src/Subtitall -c Release -r win-x64 --self-contained true
```

The NSIS installer and its `.blockmap` land in `src/Subtitall/bin/Release/net10.0/win-x64/publish/`.
Without a `GH_TOKEN` environment variable the final upload step fails — that's fine for a local
build; the installer is already produced at that point.

### Architecture

| Layer | Where | What |
|---|---|---|
| Core | `src/Subtitall/Core` | Filename parser, media library, subtitle providers, ranking, TVMaze client, rename engine, settings |
| API | `src/Subtitall/Api` | Minimal-API REST endpoints + Server-Sent Events for live progress |
| UI | `src/Subtitall/wwwroot` | VS Code-style interface — vanilla HTML/CSS/JS, zero frontend dependencies |
| Shell | `Program.cs` + ElectronNET.Core | Electron window, native dialogs, auto-update |
| Tests | `tests/Subtitall.Tests` | xUnit suite for the core logic |

Settings are stored in `%APPDATA%/Subtitall/settings.json`.

### Releasing

1. Bump `<Version>` in `src/Subtitall/Subtitall.csproj` (semver), commit (`🔖 Release vX.Y.Z`) and push.
2. Package with your GitHub token set — electron-builder builds the installer and uploads it
   (with `latest.yml` and the `.blockmap`) to a **draft** GitHub release tagged `vX.Y.Z`:

   ```bash
   GH_TOKEN=$(gh auth token) dotnet publish src/Subtitall -c Release -r win-x64 --self-contained true
   ```

3. Review the draft on GitHub, add release notes, and **publish** it — auto-update ignores drafts,
   and publishing creates the `vX.Y.Z` tag.

## License

[GPL-3.0](LICENSE)
