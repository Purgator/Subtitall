# Subtitall

[![Latest release](https://img.shields.io/github/v/release/Purgator/Subtitall)](https://github.com/Purgator/Subtitall/releases/latest)
[![License: GPL-3.0](https://img.shields.io/badge/license-GPL--3.0-blue)](LICENSE)

Find and download subtitles for your movies, TV shows and anime — a simple, lightweight desktop app with smart episode renaming.

Inspired by [Subber](https://github.com/noembryo/Subber).

## Features

- 🎬 **Link videos your way** — drag & drop files or folders anywhere in the window, or use the Add buttons. Folders are scanned recursively.
- 🧠 **Smart filename parsing** — show title, `S01E02` / `1x02` / anime absolute numbering, year and release group are detected automatically.
- 🌍 **Multiple languages** — pick any number of subtitle languages; each is saved as `video.<lang>.srt` next to the video so every media player picks it up.
- 🔎 **Four subtitle sources**
  - **Gestdown** (Addic7ed mirror) — TV shows, works out of the box, no account needed
  - **OpenSubtitles** — movies + TV, free API key
  - **SubDL** — movies + TV, free API key
  - **SubSource** (hosts the former Subscene archive) — movies + TV, free API key
- 🏆 **Best match wins** — results are ranked (exact file hash match, release-name similarity, popularity, hearing-impaired flag, provider reliability) and the best one downloads automatically. Prefer choosing yourself? Click a language chip to pick from the full list.
- 🛡️ **Built for reliability** — searches fan out to every enabled source in parallel; a source that keeps failing is put in a short cooldown instead of slowing everything down; every download is validated (an HTML error page never gets saved as `.srt`) and falls back to the next-best candidate across sources, never retrying the same source more than twice.
- ✏️ **Episode renaming** — match your files against [TVMaze](https://www.tvmaze.com) (always on, keyless) and optionally [TMDB](https://www.themoviedb.org) (free API key), preview `Show - S01E02 - Title.mkv` names, confirm, done. Results from both sources are merged; if the one you picked has no episode list, the other fills in. Subtitles sitting next to the videos are renamed too.
- 🇯🇵 **Anime-friendly** — alternate titles and acronyms are understood: files named `Shingeki no Kyojin` or `SnK` match *Attack on Titan* for both renaming and subtitle search, and absolute episode numbers (`- 59`) map onto the right season.
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
   - SubSource: create a free key from your [SubSource profile](https://subsource.net/profile) (API tab).
   - TMDB (for renaming): create a free key in your [TMDB API settings](https://www.themoviedb.org/settings/api) — the v3 key and the v4 read-access token both work.
   - Gestdown and TVMaze need nothing; Gestdown is enabled by default (TV shows only).
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
rm -rf src/Subtitall/bin/Release/net10.0/win-x64/publish
dotnet publish src/Subtitall -c Release -r win-x64 --self-contained true
```

The NSIS installer and its `.blockmap` land in `src/Subtitall/bin/Release/net10.0/win-x64/publish/`.
Delete that folder first: `dotnet publish` does not clean it, and stale files from a previous
publish would be packed into the installer. Without a `GH_TOKEN` environment variable the final
upload step fails — that's fine for a local build; the installer is already produced at that
point. Never set `GH_TOKEN` for a local package: release assets are built and uploaded by CI
only (see Releasing).

### Architecture

| Layer | Where | What |
|---|---|---|
| Core | `src/Subtitall/Core` | Filename parser, media library, subtitle providers, ranking, provider health tracking, TVMaze/TMDB clients, rename engine, settings |
| API | `src/Subtitall/Api` | Minimal-API REST endpoints + Server-Sent Events for live progress |
| UI | `src/Subtitall/wwwroot` | Simple tabbed interface — vanilla HTML/CSS/JS, zero frontend dependencies |
| Shell | `Program.cs` + ElectronNET.Core | Electron window, native dialogs, auto-update |
| Tests | `tests/Subtitall.Tests` | xUnit suite for the core logic |

Settings are stored in `%APPDATA%/Subtitall/settings.json`.

### Releasing

Releases are built by CI only — one release, one build. Never upload release assets from a
local machine: a second build on the same release can leave `latest.yml` describing a
different installer (sha512 mismatch), which breaks auto-update.

1. Bump `<Version>` in `src/Subtitall/Subtitall.csproj` (semver), commit (`🔖 Release vX.Y.Z`) and push.
2. Tag that commit and push the tag:

   ```bash
   git tag vX.Y.Z && git push origin vX.Y.Z
   ```

   The tag triggers `.github/workflows/release.yml`, which runs the tests, builds the NSIS
   installer and uploads it (with `latest.yml` and the `.blockmap`) to a **draft** GitHub
   release for `vX.Y.Z`.
3. Review the draft on GitHub, add release notes, and **publish** it. Auto-update ignores
   drafts — installed apps only see the release once it is published. Publishing does not
   re-trigger CI (the tag already exists).

## License

[GPL-3.0](LICENSE)
