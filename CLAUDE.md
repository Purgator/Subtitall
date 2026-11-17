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
   Release assets come from CI **only**: pushing the `vX.Y.Z` tag triggers
   `.github/workflows/release.yml`, which uploads the installer + `latest.yml` + blockmap to a
   draft release. Never upload release assets from a local build (never set `GH_TOKEN` on
   `dotnet publish`) — a second build on the same release breaks the `latest.yml` sha512 and
   with it auto-update. A draft is invisible to auto-update: **publishing the draft is part of
   releasing**, not optional — installed apps stay on the old version until it is published.
5. **English only** in the UI, code, comments and docs.
6. **Keep it lightweight.** The frontend is vanilla HTML/CSS/JS on purpose — no frameworks,
   no build step, no npm dependencies for the UI.

## Build & run

| Action | Command |
|---|---|
| Run desktop app (dev) | `dotnet run --project src/Subtitall` |
| Tests | `dotnet test` |
| README screenshots (`docs/*.png`, demo data, no backend) | `node tools/screenshots/capture.mjs` |
| Package Windows installer | `dotnet publish src/Subtitall -c Release -r win-x64 --self-contained true` |

Node.js 22+ must be on `PATH` (ElectronNET.Core launches Electron as a child process).
Electron/electron-builder settings live in the csproj (`<ElectronVersion>`) and
`src/Subtitall/Properties/electron-builder.json` (NSIS target + GitHub publish config) —
**not** in an `electron.manifest.json` (that's the dead legacy Electron.NET 23.x line;
this project uses the maintained `ElectronNET.Core` packages).

Packaging facts (verified 2026-10; do not "fix" these):

- The effective electron-builder `appId` is always `subtitall`: ElectronNET.Core's publish
  target passes `-c.appId` (the lowercased project name) on the CLI, overriding whatever
  `electron-builder.json` says. The json's `appId` is kept at `subtitall` so the file matches
  reality. Changing the effective appId would change the NSIS install GUID and orphan
  existing installs — don't.
- `dotnet publish` does not clean the publish folder; stale files from a previous publish get
  packed into the installer (everything under `publish/bin` ships via `extraResources`).
  Delete the publish folder before packaging locally. CI builds from a clean checkout.

## Architecture map

- `src/Subtitall/Core/` — pure logic, no HTTP endpoints:
  - `FilenameParser` (scene/anime name parsing, season hints in titles, "OVA 2" → season 0),
    `MediaNameReader` (what a file is, read locally: filename → Base64-decoded filename →
    `MatroskaTitle`, the title stored in an MKV; `MediaItem.NameSource` says which), `EpisodeGuesser`
    (season for files that only carry an episode number — pure, group-aware),
    `ShowDetectionService` (filename title → show, with aliases + episode list; asks via
    `Ambiguous` when several match; remembers user picks), `LibraryService` (linked videos),
    `SettingsService` (%APPDATA%/Subtitall/settings.json), `SubtitleNaming` (`video.<lang>.srt`),
    `MovieHash` (OpenSubtitles 64 KiB head+tail hash), `TextMatch` (token similarity),
    `TvMazeService` + `TmdbService` + `ShowMetadataService` (merged metadata sources),
    `RenameService`, `EpisodeCoverage` + `ShowInfo` (which episodes the linked files cover,
    show metadata/trailer; `GET /api/shows`), `SubtitleSearchService` (fan-out + ranking), `ProviderHealth`
    (circuit breaker + success-rate ranking input), `SubtitleValidator` (payload sanity check).
  - `Core/Providers/` — `ISubtitleProvider` implementations: `GestdownProvider` (keyless, TV),
    `OpenSubtitlesProvider` (Api-Key + optional user JWT), `SubDlProvider` (key, zip payloads),
    `SubSourceProvider` (key, X-API-Key header, zip payloads).
- `src/Subtitall/Api/` — minimal-API endpoints (`Endpoints.cs`), `EventBus` (SSE broadcast),
  `BatchSearchService` (one batch search at a time, progress events), `MediaDetectionService`
  (runs show detection + season guessing per title group after files are added, and before any
  search or rename; sets `MediaItem.Detection`/`Guess`; providers read `MediaItem.Effective`;
  files with an unusable name are first looked up by hash on OpenSubtitles),
  `UpdateService` (electron-updater events → `update-status` SSE + check/install endpoints).
- **Search feedback is part of the contract**: `SubtitleSearchService` returns a `SearchReport`
  with one `SourceOutcome` per provider (off / notApplicable / coolingDown / nothing / found /
  downloadFailed / quota / error) and the batch publishes it with every `item-result`. A
  provider that can't search a file says why through `ISubtitleProvider.CannotSearchReason`;
  a used-up download quota is a `QuotaExceededException` (tracked in `ProviderHealth`, never a
  circuit-breaker failure — searching still works). Enums serialize as camelCase strings.
- `src/Subtitall/wwwroot/` — the UI (`index.html`, `css/app.css`, `js/app.js`): a simple tabbed
  layout (Files / Rename / Shows / Re-encode / Settings). Keep it simple and intuitive; do NOT style it after an IDE
  and do not describe the app as "VS Code-like" anywhere (user's explicit request).
- **Re-encoding** (the Re-encode tab) — `Core/Reencode/` is pure and tested: `VideoInfo` (ffprobe
  JSON), `EncodePlanner` (is a file worth it, ffmpeg arguments, file names, trial maths, quality
  labels), `FfmpegRunner` (process + parsers), `FfmpegLocator` / `FfmpegDownload` (where ffmpeg is,
  which build to fetch). `Api/FfmpegService` finds or downloads ffmpeg (`ffmpeg-status` events),
  `Api/ReencodeService` runs one encode at a time, the trial on excerpts, and keep/undo
  (`reencode-changed`, `reencode-progress`, `reencode-trial` events); endpoints in
  `Api/ReencodeEndpoints.cs`.
- `Program.cs` — DI wiring, Electron window, auto-update check.

## Provider facts (verified 2026-08; do not "fix" these)

- **Podnapisi.net is dead** (domain gone since 2026-03). Do not re-add it. podnapisi.eu is a scam clone.
- **OpenSubtitles**: query params must be alphabetically sorted + lowercase, spaces as `+`;
  `file_id` for downloads comes from `attributes.files[].file_id`; an expired JWT surfaces as
  HTTP **500** with "invalid" (re-login then retry); User-Agent must be `Subtitall vX.Y.Z`;
  anonymous downloads are capped at ~5/day/IP — username/password raises the quota.
  `GET /subtitles?moviehash=…` alone identifies a file: entries with `moviehash_match: true`
  carry `feature_details` (`title`, `year`, and for episodes `parent_title`, `season_number`,
  `episode_number`) — per the API docs, not yet exercised against the live API.
- **SubDL**: uppercase language codes (`EN`, `BR_PT`, `ZH_BG`); downloads are ZIPs (sniff `PK`
  magic) except season-pack `unpack_files` URLs which are raw files; needs a browser-ish User-Agent.
- **Gestdown**: HTTP 423 = show being indexed, retry later; 2-letter language codes only
  (3-letter codes miss silently); ~50 req/min/IP; TV shows only (needs season/episode).
- **SubSource** (verified 2026-10): base `https://api.subsource.net/api/v1`, key in `X-API-Key`
  header, free tier 60 req/min / 7200/day. `GET /movies/search?searchType=text&q=…[&season=N]`
  (TV ids are per show-season), `GET /subtitles?language=…&movieId=…[&seasonNumber&episodeNumber]`,
  `GET /subtitles/{id}/download` (zip or raw; some old Subscene uploads are RAR — unsupported,
  skip to next candidate). Language names are lowercase full names with intentional quirks:
  `farsi_persian`, `brazilian_portuguese`, `chinese_bg_code` (`Languages.ToSubSource`).
  Ids arrive as JSON numbers *or* strings (`FlexibleStringConverter`).
- **TMDB**: v3 key as `api_key` query param, v4 token (starts `eyJ`) as Bearer header — both
  accepted in one settings field. Episodes come one season at a time (`/tv/{id}/season/{n}`);
  `/tv/{id}/alternative_titles` wraps TV titles in `results` (movies use `titles`);
  `/tv/{id}/videos` lists YouTube trailers (`site`, `key`, `type`, `official`) — TVMaze has
  none, so the Shows tab's trailer always comes from TMDB. Metadata only — TMDB has no subtitles. Used as an optional second rename source behind keyless TVMaze
  (`ShowMetadataService` merges by normalized name + premiere year).
- **TVMaze**: keyless; specials have `number: null` and are NOT season 0; `search/shows` wraps
  results in `{score, show}`; almost every field is nullable. `GET /shows/:id/akas` lists
  alternate titles (romaji anime names, translations); TVMaze search itself matches akas
  ("Shingeki no Kyojin" finds Attack on Titan) but NOT acronyms ("SnK" finds nothing useful) —
  acronym matching is ours (`TextMatch.MatchesShowName`).
- **Anime aliasing**: `MediaItem.AlternateTitles` comes from the detected show (name + akas);
  Gestdown and SubSource retry their show lookup with those names since they mostly index
  English titles. Rename proposals match files against the show name + akas + acronyms.
- **Specials are season 0**, and only TMDB numbers them (`/tv/{id}/season/0`, 404 when a show
  has none); `ShowMetadataService.GetEpisodesAsync` appends them to the episode list whatever
  the show's source. Everything that counts seasons filters `Season > 0`.
- **A file identified by hand** (`POST /api/library/identify`) gets a fresh `Parsed` (show name +
  season + episode, `NameSource.User`) and the show is pinned for that name — it then flows
  through the normal detection like any well-named file. A rename re-reads the new filename.
- **Show detection and season guessing are shared**: the Rename tab and the subtitle search must
  never detect a show two different ways — both go through `ShowDetectionService` +
  `EpisodeGuesser` (via `MediaDetectionService`). When in doubt, ask the user (candidate list),
  don't pick silently; a user's show pick or season override always wins.

## Re-encode facts (do not "fix" these)

- **The original is never modified.** An encode writes `name.reencoded.<ext>.part`, renames it to
  `name.reencoded.<ext>` once complete and as long as the original (`EncodePlanner.DurationMatches`),
  and only *Keep new* (`ReencodeService.Apply`) deletes the original. Deleting is permanent and
  asked for explicitly in the UI. A `.reencoded` file found next to a linked video at startup is a
  pending decision (`AdoptPending`), not a new video (`LibraryService` skips it).
- **Only the picture is encoded**: `-map 0:V` (capital V: no cover art) + audio, subtitles and
  attachments copied. MP4/M4V stay MP4 (`hvc1` tag, faststart, no attachments); everything else
  becomes MKV. Quality is constant-quality (CRF) only — no bitrate targets, no hardware encoders
  (they compress worse at equal quality, which defeats the purpose).
- **ffmpeg is never bundled** (size, and the installer must stay small): it is downloaded on
  request into `%APPDATA%/Subtitall/ffmpeg`, or found on the PATH / in the user's folder. Builds:
  BtbN `latest` release for Windows and Linux (asset names carry the version — pick the newest
  `n*` one through the GitHub API, which also gives the SHA-256 `digest`), evermeet.cx zips for
  macOS. The first ffmpeg that *runs* wins: on company computers a security policy can refuse to
  start a freshly downloaded exe (seen with Microsoft Defender's "block executables unless they
  meet a prevalence, age or trusted list criterion" rule) — that must be reported in plain words,
  never worked around.
- Linux and macOS users are offered both ways with the same weight (automatic download, or their
  system's ffmpeg with per-distribution instructions); Windows leads with the one-click install.
- The trial estimates the whole file from three 8-second excerpts: bytes of video vs. all tracks
  in the original (ffprobe packet sizes) and the encoded size of the same excerpts. The picture
  score is SSIM on those excerpts; its plain-words labels are in `EncodePlanner.QualityLabel`.

## Testing conventions

- Core services are tested with real temp directories (`TempDir` helper in `CoreServicesTests`).
- Parser cases go in `FilenameParserTests` as `[Theory]` rows — add a row for every
  naming pattern bug found in the wild.
- Provider HTTP contracts are documented above instead of mocked end-to-end; keep DTOs
  tolerant (nullable fields, `PropertyNameCaseInsensitive`).
