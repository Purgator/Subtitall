# Subtitall — AI contributor instructions

Rules for AI agents (and humans) working on this repository.

## Non-negotiable rules

1. **Gitmoji commits.** Every commit message starts with a gitmoji: ✨ feature, 🐛 fix, 📝 docs,
   ♻️ refactor, ✅ tests, 👷 CI, 🔖 release, ⬆️ dependency bump, 🎨 style/format.
   Small, focused commits — one concern per commit.
2. **README stays true.** Any change to features, commands, providers, settings or the release
   flow must update `README.md` in the same commit.
3. **No known bugs.** Run `dotnet test` before every commit. New logic gets tests in
   `tests/Subtitall.Tests`. Verify UI-facing changes by actually running the app.
4. **Semver releases.** Publish a GitHub release when a meaningful set of features/fixes has
   accumulated — follow the "Releasing" section of the README exactly. `<Version>` in
   `Subtitall.csproj` must always equal the latest tag (electron-updater compares them).
   With `GH_TOKEN` set, `dotnet publish` uploads the installer + `latest.yml` + blockmap to a
   draft release itself; publishing the draft creates the tag and activates auto-update.
5. **English only** in the UI, code, comments and docs.
6. **Keep it lightweight.** The frontend is vanilla HTML/CSS/JS on purpose — no frameworks,
   no build step, no npm dependencies for the UI.

## Build & run

| Action | Command |
|---|---|
| Run desktop app (dev) | `dotnet run --project src/Subtitall` |
| Tests | `dotnet test` |
| Package Windows installer | `dotnet publish src/Subtitall -c Release -r win-x64 --self-contained true` |

Node.js 22+ must be on `PATH` (ElectronNET.Core launches Electron as a child process).
Electron/electron-builder settings live in the csproj (`<ElectronVersion>`) and
`src/Subtitall/Properties/electron-builder.json` (NSIS target + GitHub publish config) —
**not** in an `electron.manifest.json` (that's the dead legacy Electron.NET 23.x line;
this project uses the maintained `ElectronNET.Core` packages).

## Architecture map

- `src/Subtitall/Core/` — pure logic, no HTTP endpoints:
  - `FilenameParser` (scene/anime name parsing), `LibraryService` (linked videos),
    `SettingsService` (%APPDATA%/Subtitall/settings.json), `SubtitleNaming` (`video.<lang>.srt`),
    `MovieHash` (OpenSubtitles 64 KiB head+tail hash), `TextMatch` (token similarity),
    `TvMazeService`, `RenameService`, `SubtitleSearchService` (fan-out + ranking).
  - `Core/Providers/` — `ISubtitleProvider` implementations: `GestdownProvider` (keyless, TV),
    `OpenSubtitlesProvider` (Api-Key + optional user JWT), `SubDlProvider` (key, zip payloads).
- `src/Subtitall/Api/` — minimal-API endpoints (`Endpoints.cs`), `EventBus` (SSE broadcast),
  `BatchSearchService` (one batch search at a time, progress events).
- `src/Subtitall/wwwroot/` — the VS Code-style UI (`index.html`, `css/app.css`, `js/app.js`).
- `Program.cs` — DI wiring, Electron window, auto-update check.

## Provider facts (verified 2026-08; do not "fix" these)

- **Podnapisi.net is dead** (domain gone since 2026-03). Do not re-add it. podnapisi.eu is a scam clone.
- **OpenSubtitles**: query params must be alphabetically sorted + lowercase, spaces as `+`;
  `file_id` for downloads comes from `attributes.files[].file_id`; an expired JWT surfaces as
  HTTP **500** with "invalid" (re-login then retry); User-Agent must be `Subtitall vX.Y.Z`;
  anonymous downloads are capped at ~5/day/IP — username/password raises the quota.
- **SubDL**: uppercase language codes (`EN`, `BR_PT`, `ZH_BG`); downloads are ZIPs (sniff `PK`
  magic) except season-pack `unpack_files` URLs which are raw files; needs a browser-ish User-Agent.
- **Gestdown**: HTTP 423 = show being indexed, retry later; 2-letter language codes only
  (3-letter codes miss silently); ~50 req/min/IP; TV shows only (needs season/episode).
- **TVMaze**: keyless; specials have `number: null` and are NOT season 0; `search/shows` wraps
  results in `{score, show}`; almost every field is nullable.

## Testing conventions

- Core services are tested with real temp directories (`TempDir` helper in `CoreServicesTests`).
- Parser cases go in `FilenameParserTests` as `[Theory]` rows — add a row for every
  naming pattern bug found in the wild.
- Provider HTTP contracts are documented above instead of mocked end-to-end; keep DTOs
  tolerant (nullable fields, `PropertyNameCaseInsensitive`).
