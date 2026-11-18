// Fictional demo data for the README screenshots. Every show, file name, release group and
// account below is made up; nothing here is read from a real settings file or a real provider.
// The shapes mirror what the API in src/Subtitall/Api/Endpoints.cs returns.

const pad = (n) => String(n).padStart(2, "0");

/** ISO date `days` away from today, so "aired" / "not aired yet" stay true whenever this runs. */
function day(days) {
  const d = new Date();
  d.setDate(d.getDate() + days);
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}`;
}

/** A drawn poster (no real artwork): gradient, a simple motif and the show's name. */
function poster(lines, from, to, motif) {
  const text = lines
    .map((line, i) => `<text x="105" y="${232 + i * 26}" text-anchor="middle" font-family="Segoe UI, Arial, sans-serif" font-size="21" font-weight="700" fill="#fff">${line}</text>`)
    .join("");
  const svg = `<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 210 295">
    <defs><linearGradient id="g" x1="0" y1="0" x2="0.4" y2="1"><stop offset="0" stop-color="${from}"/><stop offset="1" stop-color="${to}"/></linearGradient></defs>
    <rect width="210" height="295" fill="url(#g)"/>${motif}${text}</svg>`;
  return `data:image/svg+xml;base64,${Buffer.from(svg).toString("base64")}`;
}

const posters = {
  harbor: poster(["HARBOR", "LIGHTS"], "#12314f", "#0a1626",
    `<path d="M96 60h18l8 120H88z" fill="#e8edf5"/><rect x="92" y="44" width="26" height="18" rx="3" fill="#ffd66b"/>
     <path d="M118 52l70-22v44z" fill="#ffd66b" opacity=".28"/><path d="M0 182q26-12 52 0t52 0 52 0 54 0v20H0z" fill="#1d5a8a" opacity=".8"/>`),
  starlight: poster(["STARLIGHT", "COURIERS"], "#3a1d6e", "#120a2a",
    `<circle cx="150" cy="70" r="30" fill="#ffe9a8" opacity=".9"/><circle cx="40" cy="50" r="2.5" fill="#fff"/><circle cx="78" cy="96" r="2" fill="#fff"/>
     <circle cx="60" cy="150" r="2.5" fill="#fff"/><circle cx="170" cy="150" r="2" fill="#fff"/><path d="M30 178l80-52 14 14-70 46z" fill="#ff8fb3"/>
     <path d="M110 126l34-12-20 26z" fill="#ffd0de"/>`),
  meridianA: poster(["MERIDIAN"], "#5a2a1a", "#1c0e0a",
    `<circle cx="105" cy="110" r="62" fill="none" stroke="#f3b27a" stroke-width="3"/><path d="M105 48v124M43 110h124" stroke="#f3b27a" stroke-width="2"/>
     <ellipse cx="105" cy="110" rx="28" ry="62" fill="none" stroke="#f3b27a" stroke-width="2"/>`),
  meridianB: poster(["MERIDIAN"], "#134b45", "#081c1a",
    `<path d="M20 170l44-70 30 40 34-60 62 90z" fill="#3fbfa9" opacity=".85"/><circle cx="160" cy="62" r="18" fill="#d9fff6" opacity=".9"/>`),
};

// ---- Shows -------------------------------------------------------------------------------------

const harborLights = {
  show: { id: 9101, name: "Harbor Lights", premiered: day(-420), network: "Channel North", imageUrl: posters.harbor, source: "TVMaze" },
  aliases: ["Harbor Lights"],
  info: {
    summary: "When the keeper of a remote lighthouse vanishes, his estranged daughter comes back to a fishing town that would rather keep its secrets under water.",
    genres: ["Drama", "Mystery"], status: "Running", rating: 8.4,
    officialSite: "https://example.com/harbor-lights", trailerUrl: "https://example.com/harbor-lights/trailer",
  },
  seasons: [
    { season: 1, firstAir: -420, titles: ["The Tide Comes In", "Salt and Smoke", "Low Water", "The Night Ferry", "Signal Fire", "What the Sea Keeps"] },
    { season: 2, firstAir: -14, titles: ["Storm Glass", "The Second Lamp", "Breakwater", "Fog Bank", "Dead Reckoning", "Landfall"] },
  ],
};

const starlightCouriers = {
  show: { id: 9102, name: "Starlight Couriers", premiered: day(-600), network: "Tokyo Sora", imageUrl: posters.starlight, source: "TVMaze" },
  aliases: ["Starlight Couriers", "Hoshi no Haitatsunin"],
  info: {
    summary: "Two rookie couriers deliver letters between drifting sky islands, one overdue parcel at a time.",
    genres: ["Anime", "Adventure", "Fantasy"], status: "Ended", rating: 7.9, officialSite: null, trailerUrl: null,
  },
  seasons: [
    { season: 1, firstAir: -600, titles: Array.from({ length: 12 }, (_, i) => `Delivery ${i + 1}`) },
    { season: 2, firstAir: -240, titles: ["Return to Sender", "The Long Way Round", "Postmarked Tomorrow", "Fragile", "Night Shift", "Lost and Found", "Signed For", "Tailwind", "Dead Letter", "Express", "Last Stop", "Starlight"] },
  ],
};

const meridianCandidates = [
  { id: 9201, name: "Meridian", premiered: "2019-03-08", network: "Atlas One", imageUrl: posters.meridianA, source: "TVMaze" },
  { id: 9202, name: "Meridian", premiered: "2004-09-21", network: "Westbrook TV", imageUrl: posters.meridianB, source: "TVMaze" },
];

function episodesOf(show) {
  return show.seasons.flatMap((s) => s.titles.map((name, i) => ({
    season: s.season, number: i + 1, name, airdate: day(s.firstAir + i * 7),
  })));
}

// ---- Linked files ------------------------------------------------------------------------------

const GB = 1024 ** 3;
const folder = "D:\\Videos\\Series";
let nextId = 1;

function file(fileName, sizeGb, parsed, extra = {}) {
  return {
    id: `demo${nextId++}`, fileName, path: `${folder}\\${fileName}`, sizeBytes: Math.round(sizeGb * GB),
    parsed: {
      title: "", season: null, episode: null, endEpisode: null, absoluteEpisode: null, year: null,
      releaseGroup: null, seasonHint: null, finalSeasonHint: false, isEpisode: true, ...parsed,
    },
    effective: { season: parsed.season ?? null, episode: parsed.episode ?? null, endEpisode: null },
    guess: null, seasonOverride: null, detection: null, subtitles: {},
    ...extra,
  };
}

function resolved(show, title) {
  return {
    title, status: "resolved", chosenByUser: false, show: show.show,
    seasons: show.seasons.map((s) => s.season), candidates: [show.show],
  };
}

function harborFile(season, episode, quality, group, sizeGb, subtitles) {
  return file(`Harbor.Lights.S${pad(season)}E${pad(episode)}.${quality}-${group}.mkv`, sizeGb,
    { title: "Harbor Lights", season, episode, releaseGroup: group },
    { detection: resolved(harborLights, "Harbor Lights"), subtitles, show: harborLights });
}

function starlightFile(absolute, sizeGb, subtitles) {
  const episode = absolute - 12;
  return file(`[Komorebi] Hoshi no Haitatsunin - ${absolute} [1080p].mkv`, sizeGb,
    { title: "Hoshi no Haitatsunin", absoluteEpisode: absolute, releaseGroup: "Komorebi" },
    {
      effective: { season: 2, episode, endEpisode: null },
      guess: { season: 2, episode, confidence: "high", reason: `Episode ${absolute} overall (absolute numbering)` },
      detection: resolved(starlightCouriers, "Hoshi no Haitatsunin"), subtitles, show: starlightCouriers,
    });
}

function meridianFile(episode, sizeGb) {
  return file(`Meridian.S01E${pad(episode)}.720p.HDTV.x264-KESTREL.mkv`, sizeGb,
    { title: "Meridian", season: 1, episode, releaseGroup: "KESTREL" },
    { detection: { title: "Meridian", status: "ambiguous", chosenByUser: false, show: null, seasons: [], candidates: meridianCandidates } });
}

const both = { en: true, fr: true };
const enOnly = { en: true, fr: false };

const files = [
  starlightFile(13, 1.3, both),
  starlightFile(14, 1.3, both),
  starlightFile(15, 1.4, enOnly),
  meridianFile(1, 0.9),
  meridianFile(2, 0.9),
  harborFile(1, 1, "1080p.WEB.H264", "TIDEWATER", 2.1, both),
  harborFile(1, 2, "1080p.WEB.H264", "TIDEWATER", 2.0, both),
  harborFile(1, 3, "1080p.WEB.H264", "TIDEWATER", 2.2, both),
  harborFile(1, 5, "1080p.WEB.H264", "TIDEWATER", 2.1, enOnly),
  harborFile(1, 6, "1080p.WEB.H264", "TIDEWATER", 2.4, {}),
  harborFile(2, 1, "2160p.WEB.H265", "NORTHWIND", 5.8, {}),
  harborFile(2, 2, "2160p.WEB.H265", "NORTHWIND", 6.1, {}),
  file("The.Clockmakers.Garden.2019.1080p.BluRay.x264-ORCHARD.mkv", 9.7,
    { title: "The Clockmakers Garden", year: 2019, releaseGroup: "ORCHARD", isEpisode: false }),
];

/** What GET /api/library returns (the internal `show` link is dropped). */
// The group each file is shown in, as the backend's MediaGrouper would place it.
function groupOf(item) {
  const base = { block: null, folder: "Series" };
  if (!item.parsed.isEpisode) return { ...base, key: "other", kind: "other", name: "Other files" };
  const show = item.detection?.show;
  const block = `${item.parsed.title}|${item.parsed.season ?? "bare"}`.toLowerCase();
  return show
    ? { ...base, key: `show:${show.source}:${show.id}`, kind: "show", name: show.name, block }
    : { ...base, key: `title:${item.parsed.title.toLowerCase()}`, kind: "title", name: item.parsed.title, block };
}

export const library = files.map(({ show, ...item }) => ({ ...item, group: groupOf(item) }));

const byName = (part) => files.find((f) => f.fileName.includes(part)).id;

// ---- Settings, providers ------------------------------------------------------------------------

export const meta = {
  version: "0.7.1", // overwritten with the csproj <Version> by capture.mjs
  isElectron: true,
  languages: [
    ["en", "English"], ["fr", "French"], ["es", "Spanish"], ["de", "German"], ["it", "Italian"],
    ["pt-pt", "Portuguese"], ["pt-br", "Portuguese (Brazil)"], ["nl", "Dutch"], ["pl", "Polish"],
    ["ru", "Russian"], ["uk", "Ukrainian"], ["ja", "Japanese"], ["ko", "Korean"],
    ["zh-cn", "Chinese (Simplified)"], ["zh-tw", "Chinese (Traditional)"], ["ar", "Arabic"],
    ["tr", "Turkish"], ["sv", "Swedish"], ["no", "Norwegian"], ["da", "Danish"], ["fi", "Finnish"],
    ["cs", "Czech"], ["sk", "Slovak"], ["el", "Greek"], ["he", "Hebrew"], ["hu", "Hungarian"],
    ["ro", "Romanian"], ["bg", "Bulgarian"], ["hr", "Croatian"], ["sr", "Serbian"],
    ["sl", "Slovenian"], ["id", "Indonesian"], ["ms", "Malay"], ["th", "Thai"],
    ["vi", "Vietnamese"], ["hi", "Hindi"], ["fa", "Persian"], ["et", "Estonian"],
    ["lv", "Latvian"], ["lt", "Lithuanian"],
  ].map(([code, name]) => ({ code, name })),
  // As in an official build: OpenSubtitles and TMDB work without the user entering a key.
  builtInKeys: { openSubtitles: true, tmdb: true },
};

const noKey = { enabled: false, apiKey: "", username: "", password: "" };

/** No key or password is ever present: the screenshots show the "built-in key" placeholders. */
export const settings = {
  languages: ["en", "fr"],
  openSubtitles: { ...noKey, enabled: true },
  subDl: { ...noKey },
  subSource: { ...noKey },
  tmdb: { ...noKey, enabled: true },
  gestdown: { ...noKey, enabled: true },
  renameTemplate: "{Show} - S{Season}E{Episode} - {Title}",
  overwriteExisting: false,
};

export const providerStatus = [
  { name: "OpenSubtitles", configured: true, cooldownUntil: null, quota: { remaining: 3, resetAt: null, exhausted: false }, account: { hasCredentials: false, login: null } },
  { name: "SubDL", configured: false, cooldownUntil: null, quota: null, account: null },
  { name: "Gestdown", configured: true, cooldownUntil: null, quota: null, account: null },
  { name: "SubSource", configured: false, cooldownUntil: null, quota: null, account: null },
];

export const updateStatus = { state: "upToDate", version: null, percent: null, message: "", at: new Date().toISOString() };

// ---- A search in progress (Files tab) --------------------------------------------------------------

const off = (provider) => ({ provider, status: "off", found: 0, detail: "not enabled or no API key" });
const found = (provider, n) => ({ provider, status: "found", found: n, detail: `${n} found` });
const nothing = (provider) => ({ provider, status: "nothing", found: 0, detail: "nothing for this file in this language" });

/** Events the UI receives over /api/events while a batch search runs, replayed on connect. */
export function searchEvents() {
  const total = library.length * 2;
  let done = 0;
  const events = [{ type: "batch-started", data: { total } }];
  const result = (id, language, downloaded, sources) => events.push({
    type: "item-result",
    data: { itemId: id, language, downloaded, skipped: false, provider: downloaded ? "OpenSubtitles" : null, error: downloaded ? null : "No subtitles found", sources, done: ++done, total },
  });

  for (const item of files.slice(0, 5)) {
    for (const language of ["en", "fr"]) {
      const had = !!item.subtitles[language];
      const isAnimeFr = item.fileName.includes("- 15") && language === "fr";
      if (item.parsed.title === "Meridian") {
        result(item.id, language, language === "en", language === "en"
          ? [found("OpenSubtitles", 6), off("SubDL"), found("Gestdown", 2), off("SubSource")]
          : [nothing("OpenSubtitles"), off("SubDL"), nothing("Gestdown"), off("SubSource")]);
      } else {
        result(item.id, language, had, isAnimeFr
          ? [nothing("OpenSubtitles"), off("SubDL"), nothing("Gestdown"), off("SubSource")]
          : [found("OpenSubtitles", 4), off("SubDL"), nothing("Gestdown"), off("SubSource")]);
      }
    }
  }
  for (const [part, language] of [["S01E01", "en"], ["S01E01", "fr"], ["S01E02", "en"], ["S01E02", "fr"], ["S01E03", "en"]])
    result(byName(`Harbor.Lights.${part}`), language, true, [found("OpenSubtitles", 9), off("SubDL"), found("Gestdown", 3), off("SubSource")]);
  events.push({ type: "item-searching", data: { itemId: byName("Harbor.Lights.S01E03"), language: "fr", done, total } });
  return events;
}

// ---- Subtitle picker (click on a chip) -----------------------------------------------------------------

export const pickerItemId = byName("Harbor.Lights.S02E01");

export function candidates() {
  const c = (provider, releaseName, score, downloads, flags = {}) => ({
    provider, downloadRef: "demo", language: "en", releaseName, downloads,
    hashMatch: false, hearingImpaired: false, score, ...flags,
  });
  return {
    candidates: [
      c("OpenSubtitles", "Harbor.Lights.S02E01.2160p.WEB.H265-NORTHWIND", 168, 2841, { hashMatch: true }),
      c("OpenSubtitles", "Harbor.Lights.S02E01.1080p.WEB.H264-TIDEWATER", 74, 5312),
      c("Gestdown", "Harbor Lights - 02x01 - Storm Glass.NORTHWIND+TIDEWATER", 71, 1207),
      c("Gestdown", "Harbor Lights - 02x01 - Storm Glass.NORTHWIND+TIDEWATER", 63, 388, { hearingImpaired: true }),
      c("OpenSubtitles", "Harbor.Lights.S02E01.720p.HDTV.x264-KESTREL", 52, 964),
      c("OpenSubtitles", "Harbor Lights S02E01 Storm Glass 1080p WEB-DL", 47, 211, { hearingImpaired: true }),
    ],
    sources: [found("OpenSubtitles", 4), off("SubDL"), found("Gestdown", 2), off("SubSource")],
  };
}

// ---- Rename tab ----------------------------------------------------------------------------------------

export function proposals() {
  return files.map((item) => {
    const base = { itemId: item.id, oldName: item.fileName, newName: item.fileName, problem: null, canApply: false, note: null };
    if (!item.parsed.isEpisode) return { ...base, problem: "Not a TV episode" };
    if (!item.show) return { ...base, problem: "Several shows match — pick one" };
    const { season, episode } = item.effective;
    const title = item.show.seasons.find((s) => s.season === season).titles[episode - 1];
    return {
      ...base, canApply: true,
      newName: `${item.show.show.name} - S${pad(season)}E${pad(episode)} - ${title}.mkv`,
      note: item.guess?.reason ?? null,
    };
  });
}

// ---- Shows tab -----------------------------------------------------------------------------------------

const today = day(0);

function coverage(show) {
  const linked = new Map(files.filter((f) => f.show === show).map((f) => [`${f.effective.season}:${f.effective.episode}`, f.fileName]));
  const seasons = show.seasons.map((s) => {
    const episodes = episodesOf(show).filter((e) => e.season === s.season).map((e) => {
      const fileName = linked.get(`${e.season}:${e.number}`) ?? null;
      return { ...e, aired: e.airdate <= today, have: fileName !== null, fileName };
    });
    return {
      season: s.season, episodes,
      aired: episodes.filter((e) => e.aired).length,
      have: episodes.filter((e) => e.have).length,
      missing: episodes.filter((e) => e.aired && !e.have).length,
      unaired: episodes.filter((e) => !e.aired).length,
    };
  });
  const sum = (key) => seasons.reduce((n, s) => n + s[key], 0);
  return { seasons, aired: sum("aired"), have: sum("have"), missing: sum("missing"), unaired: sum("unaired"), files: linked.size };
}

const demoShows = [harborLights, starlightCouriers];

export function shows() {
  return demoShows.map((s) => {
    const c = coverage(s);
    return { show: s.show, files: c.files, seasons: c.seasons.length, aired: c.aired, have: c.have, missing: c.missing, unaired: c.unaired };
  });
}

export function showDetails(id) {
  const s = demoShows.find((x) => x.show.id === id);
  if (!s) return null;
  const last = s.seasons.at(-1);
  return {
    show: s.show,
    info: {
      name: s.show.name, ...s.info, premiered: s.show.premiered,
      ended: s.info.status === "Ended" ? day(last.firstAir + (last.titles.length - 1) * 7) : null,
      network: s.show.network, imageUrl: s.show.imageUrl, source: s.show.source,
    },
    aliases: s.aliases,
    ...coverage(s),
  };
}

// ---- Re-encode tab: one season half done ---------------------------------------------------------


function videoOf(item) {
  const name = item.fileName;
  const uhd = name.includes("2160p"), sd = name.includes("720p");
  const [width, height] = uhd ? [3840, 2160] : sd ? [1280, 720] : [1920, 1080];
  const codec = name.includes("H265") ? "hevc" : "h264";
  return {
    codec, codecName: codec === "hevc" ? "H.265" : "H.264", width, height,
    bitRate: Math.round(item.sizeBytes * 8 / (name.includes("Clockmakers") ? 6900 : 2580)),
    durationSeconds: name.includes("Clockmakers") ? 6900 : 2580, hdr: false,
  };
}

/** What POST /api/reencode/analyze returns: every file, and whether it is worth re-encoding. */
export function reencodeFiles() {
  return library.map((item) => {
    const info = videoOf(item);
    const efficient = info.codec === "hevc";
    return {
      itemId: item.id, info,
      advice: {
        kind: efficient ? "alreadyEfficient" : "recommended", recommended: !efficient, possible: true,
        reason: efficient ? "Already H.265 — little to gain, some quality to lose" : "",
      },
    };
  });
}

/** What GET /api/reencode/status returns: ffmpeg installed, a season of Harbor Lights under way. */
export function reencodeStatus() {
  const season = library.filter((i) => i.fileName.startsWith("Harbor.Lights.S01"));
  const job = (item, state, extra = {}) => ({
    itemId: item.id, state, percent: 0, speed: 0, etaSeconds: null, originalBytes: item.sizeBytes, newBytes: 0,
    gainPercent: null, ssim: null, quality: null, error: null, codec: "hevc", level: "balanced", ...extra,
  });
  const done = (item, newGb, ssim) => job(item, "done", {
    percent: 100, newBytes: Math.round(newGb * GB), gainPercent: Math.round((1 - newGb * GB / item.sizeBytes) * 1000) / 10,
    ssim, quality: "Visually identical",
  });
  return {
    ffmpeg: {
      state: "ready", os: "windows", canDownload: true, source: "app", version: "8.0",
      path: "C:\\Users\\you\\AppData\\Roaming\\Subtitall\\ffmpeg\\ffmpeg.exe",
      encoders: ["libx265", "libx264", "libsvtav1"], percent: null, downloadedBytes: 0, totalBytes: 0, message: null,
      blocked: false, hasDownload: true, fallbacks: ["winget (Windows' package manager)"], tried: [],
      installFolder: "C:\\Users\\you\\AppData\\Roaming\\Subtitall\\ffmpeg",
    },
    state: {
      running: true,
      jobs: [
        done(season[0], 0.84, 0.9912),
        done(season[1], 0.77, 0.9905),
        job(season[2], "encoding", { percent: 42.3, speed: 1.8, etaSeconds: 780 }),
        job(season[3], "queued"),
        job(season[4], "queued"),
      ],
      trial: null,
    },
  };
}
