// Subtitall UI — vanilla JS, no dependencies.

// ---------- API helpers ------------------------------------------------------

async function api(method, url, body) {
  const options = { method, headers: {} };
  if (body !== undefined) {
    options.headers["Content-Type"] = "application/json";
    options.body = JSON.stringify(body);
  }
  const response = await fetch(url, options);
  if (!response.ok) {
    let message = `${response.status} ${response.statusText}`;
    try {
      const data = await response.json();
      message = data.message || data.detail || data.title || message;
    } catch { /* not JSON */ }
    throw new Error(message);
  }
  if (response.status === 204 || response.status === 202) return null;
  const text = await response.text();
  return text ? JSON.parse(text) : null;
}

const get = (url) => api("GET", url);
const post = (url, body) => api("POST", url, body);
const put = (url, body) => api("PUT", url, body);
const del = (url) => api("DELETE", url);

// ---------- State ------------------------------------------------------------

const state = {
  view: "files",
  meta: { version: "", isElectron: false, languages: [] },
  settings: null,
  items: [],
  searching: false,
  progress: { done: 0, total: 0 },
  langState: {},          // itemId -> { lang: "searching" | "failed:<msg>" }
  sources: {},            // itemId -> { lang: [{provider, status, found, detail}] } from the last search
  providerStatus: null,   // /api/providers/status
  // shows view
  shows: null,            // /api/shows (null = not loaded yet)
  selectedShow: null,     // "source:id"
  showDetails: null,      // /api/shows/{source}/{id}
  update: null,           // /api/update/status
  // rename view
  proposals: null,        // null = not loaded yet
  proposalChecks: {},     // itemId -> bool
  renameBusy: false,
};

// ---------- Utilities ----------------------------------------------------------

function esc(value) {
  return String(value ?? "").replace(/[&<>"']/g, (c) =>
    ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
}

function sourceClass(status) {
  switch (status) {
    case "found": return "ok";
    case "nothing": case "notApplicable": case "off": return "dim";
    default: return "warn";
  }
}

function formatSize(bytes) {
  if (!bytes) return "";
  const units = ["B", "KB", "MB", "GB"];
  let i = 0, n = bytes;
  while (n >= 1024 && i < units.length - 1) { n /= 1024; i++; }
  return `${n.toFixed(n >= 100 || i === 0 ? 0 : 1)} ${units[i]}`;
}

function toast(message, kind = "info", ms = 4200) {
  const el = document.createElement("div");
  el.className = `toast ${kind}`;
  el.textContent = message;
  document.getElementById("toast-root").appendChild(el);
  setTimeout(() => el.remove(), ms);
}

function debounce(fn, ms) {
  let timer;
  return (...args) => { clearTimeout(timer); timer = setTimeout(() => fn(...args), ms); };
}

function activeProviders() {
  const s = state.settings;
  if (!s) return [];
  return [
    s.gestdown?.enabled ? "Gestdown" : null,
    s.openSubtitles?.enabled && (s.openSubtitles?.apiKey || state.meta?.builtInKeys?.openSubtitles) ? "OpenSubtitles" : null,
    s.subDl?.enabled && s.subDl?.apiKey ? "SubDL" : null,
    s.subSource?.enabled && s.subSource?.apiKey ? "SubSource" : null,
  ].filter(Boolean);
}

// ---------- Data loading -----------------------------------------------------------

async function loadLibrary() {
  state.items = await get("/api/library");
  // Don't nuke the Settings form (and any unsaved input) over a library change —
  // that view doesn't display library data anyway.
  if (state.view === "settings") renderTopbar();
  else render();
}

const loadLibraryDebounced = debounce(() => loadLibrary().catch(console.error), 250);
const loadProposalsDebounced = debounce(() => loadProposals().catch(console.error), 400);

async function loadAll() {
  const [meta, settings, items, providerStatus, update] = await Promise.all([
    get("/api/meta"), get("/api/settings"), get("/api/library"),
    get("/api/providers/status").catch(() => null), get("/api/update/status").catch(() => null),
  ]);
  state.meta = meta;
  state.settings = settings;
  state.items = items;
  state.providerStatus = providerStatus;
  state.update = update;
}

// ---------- Server-sent events -------------------------------------------------------

function connectEvents() {
  const source = new EventSource("/api/events");
  // On (re)connect, resync batch state — a batch-finished event may have been
  // missed while disconnected, which would leave the UI stuck in "searching".
  source.onopen = async () => {
    try {
      const status = await get("/api/subtitles/status");
      if (!status.running && state.searching) {
        state.searching = false;
        loadLibraryDebounced();
      }
    } catch { /* backend not ready yet */ }
  };
  source.onmessage = (event) => {
    const { type, data } = JSON.parse(event.data);
    switch (type) {
      case "library-changed":
      case "settings-changed":
        // The settings form saves itself and must not be rebuilt under the user's cursor.
        if (type === "settings-changed") get("/api/settings").then((s) => { state.settings = s; if (state.view !== "settings") render(); });
        loadLibraryDebounced();
        if (state.view === "rename" && state.proposals !== null && !state.renameBusy) loadProposalsDebounced();
        if (state.shows !== null) { state.showDetails = null; loadShowsDebounced(); }
        break;
      case "batch-started":
        state.searching = true;
        state.progress = { done: 0, total: data.total };
        state.langState = {};
        state.sources = {};
        render();
        break;
      case "item-searching": {
        (state.langState[data.itemId] ??= {})[data.language] = "searching";
        state.progress.done = data.done;
        renderTopbar();
        renderFilesRow(data.itemId);
        break;
      }
      case "item-result": {
        const langs = (state.langState[data.itemId] ??= {});
        if (data.sources?.length) (state.sources[data.itemId] ??= {})[data.language] = data.sources;
        if (data.downloaded || data.skipped) {
          delete langs[data.language];
          const item = state.items.find((i) => i.id === data.itemId);
          if (item) item.subtitles[data.language] = data.downloaded || item.subtitles[data.language];
        } else {
          langs[data.language] = `failed:${data.error || "No subtitles found"}`;
        }
        state.progress.done = data.done;
        renderTopbar();
        renderFilesRow(data.itemId);
        break;
      }
      case "update-status":
        state.update = data;
        renderUpdateStatus();
        if (data.state === "downloaded") toast(data.message, "ok", 10000);
        else if (data.state === "error") toast(data.message, "err", 8000);
        break;
      case "batch-finished": {
        state.searching = false;
        const parts = [`${data.downloaded} downloaded`];
        if (data.skipped) parts.push(`${data.skipped} already there`);
        if (data.failed) parts.push(`${data.failed} not found`);
        toast(`Search ${data.cancelled ? "cancelled" : "finished"} — ${parts.join(", ")}`,
          data.downloaded > 0 ? "ok" : (data.failed > 0 ? "err" : "info"), 6000);
        loadLibraryDebounced();
        loadProviderStatus();
        break;
      }
    }
  };
}

// ---------- Drag & drop -----------------------------------------------------------------

function getDroppedPaths(event) {
  let webUtils = null;
  try { webUtils = window.require ? window.require("electron").webUtils : null; } catch { /* browser mode */ }
  return [...event.dataTransfer.files]
    .map((file) => {
      try { if (webUtils) return webUtils.getPathForFile(file); } catch { /* fall through */ }
      return file.path || null;
    })
    .filter(Boolean);
}

function setupDragDrop() {
  const overlay = document.getElementById("drop-overlay");
  let depth = 0;
  window.addEventListener("dragenter", (e) => { e.preventDefault(); if (++depth === 1) overlay.classList.add("visible"); });
  window.addEventListener("dragleave", (e) => { e.preventDefault(); if (--depth <= 0) { depth = 0; overlay.classList.remove("visible"); } });
  window.addEventListener("dragover", (e) => e.preventDefault());
  window.addEventListener("drop", async (e) => {
    e.preventDefault();
    depth = 0;
    overlay.classList.remove("visible");
    const paths = getDroppedPaths(e);
    if (paths.length === 0) {
      if (!state.meta.isElectron) toast("Drag & drop needs the desktop app — use the Add buttons instead", "err");
      return;
    }
    try {
      const result = await post("/api/library/add", { paths });
      toast(result.added > 0 ? `Added ${result.added} video${result.added > 1 ? "s" : ""}` : "No new videos found", result.added > 0 ? "ok" : "info");
    } catch (err) {
      toast(`Could not add files: ${err.message}`, "err");
    }
  });
}

// ---------- Rendering: shell ----------------------------------------------------------------

function render() {
  document.querySelectorAll(".tab").forEach((tab) =>
    tab.classList.toggle("active", tab.dataset.view === state.view));
  renderTopbar();
  renderMain();
}

function renderTopbar() {
  document.getElementById("top-version").textContent = `v${state.meta.version}`;
  renderUpdateStatus();
  const progress = document.getElementById("top-progress");
  progress.hidden = !state.searching;
  if (state.searching)
    document.getElementById("top-progress-text").textContent =
      `Searching ${state.progress.done}/${state.progress.total}`;
}

function renderMain() {
  const main = document.getElementById("main");
  if (state.view === "files") renderFilesView(main);
  else if (state.view === "rename") renderRenameView(main);
  else if (state.view === "shows") renderShowsView(main);
  else renderSettingsView(main);
}

// ---------- Files view --------------------------------------------------------------

async function browse(mode) {
  if (!state.meta.isElectron) {
    const path = prompt(`Desktop dialogs are unavailable in the browser.\nEnter a ${mode === "folder" ? "folder" : "file"} path:`);
    if (!path) return;
    try {
      const result = await post("/api/library/add", { paths: [path] });
      toast(`Added ${result.added} video${result.added === 1 ? "" : "s"}`, result.added ? "ok" : "info");
    } catch (err) { toast(err.message, "err"); }
    return;
  }
  try {
    const result = await post("/api/library/browse", { mode });
    if (result.added > 0) toast(`Added ${result.added} video${result.added === 1 ? "" : "s"}`, "ok");
  } catch (err) {
    toast(err.message, "err");
  }
}

function renderFilesView(main) {
  const providers = activeProviders();
  const languages = state.settings?.languages || [];

  if (!state.items.length) {
    main.innerHTML = `
      <div class="dropzone">
        <svg viewBox="0 0 24 24"><path d="M12 16V5m0 0l-4 4m4-4l4 4" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round"/><path d="M4 15v3a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2v-3" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round"/></svg>
        <div class="title">Drop your videos or folders here</div>
        <div>Subtitles are found automatically and saved next to each video.</div>
        <div class="actions">
          <button class="btn" id="empty-add-files">Add videos…</button>
          <button class="btn secondary" id="empty-add-folder">Add a folder…</button>
        </div>
      </div>`;
    main.querySelector("#empty-add-files").onclick = () => browse("files");
    main.querySelector("#empty-add-folder").onclick = () => browse("folder");
    return;
  }

  main.innerHTML = `
    <div class="actionbar">
      <button class="btn secondary" id="add-files">+ Add videos…</button>
      <button class="btn secondary" id="add-folder">+ Add folder…</button>
      <button class="btn secondary danger" id="clear-list">Clear</button>
      <span class="hint-text">${state.items.length} video${state.items.length > 1 ? "s" : ""} · languages: <b>${esc(languages.join(", "))}</b></span>
      <div class="grow"></div>
      ${providers.length === 0
        ? `<span class="warn-text">No subtitle provider configured — <span class="linkish" id="goto-settings">open Settings</span></span>`
        : `<span class="hint-text" id="sources-line">${sourcesLineHtml()}</span>
           <span class="hint-text">· click a chip for details or to pick a subtitle</span>`}
      ${state.searching
        ? `<button class="btn danger" id="cancel-search">Cancel search</button>`
        : `<button class="btn" id="start-search" ${providers.length ? "" : "disabled"}>Search subtitles</button>`}
    </div>
    <div class="card">
      <table class="file-table">
        <thead><tr>
          <th class="col-langs">Subtitles</th>
          <th>File</th>
          <th class="col-parsed">Detected</th>
          <th class="col-size">Size</th>
          <th class="col-actions"></th>
        </tr></thead>
        <tbody>${state.items.map(renderFileRowHtml).join("")}</tbody>
      </table>
    </div>`;

  main.querySelector("#add-files").onclick = () => browse("files");
  main.querySelector("#add-folder").onclick = () => browse("folder");
  main.querySelector("#clear-list").onclick = () => del("/api/library").catch((e) => toast(e.message, "err"));
  const gotoSettings = main.querySelector("#goto-settings");
  if (gotoSettings) gotoSettings.onclick = () => { state.view = "settings"; render(); };
  const startBtn = main.querySelector("#start-search");
  if (startBtn) startBtn.onclick = () => post("/api/subtitles/search", {}).catch((e) => toast(e.message, "err"));
  const cancelBtn = main.querySelector("#cancel-search");
  if (cancelBtn) cancelBtn.onclick = () => post("/api/subtitles/cancel");
  bindFileRowActions(main);
}

function renderFileRowHtml(item) {
  const languages = state.settings?.languages || [];
  const langStates = state.langState[item.id] || {};
  const chips = languages.map((lang) => {
    const st = langStates[lang];
    const sources = state.sources[item.id]?.[lang];
    let cls = "", title = `Search ${lang} subtitles for this file`;
    if (st === "searching") { cls = "searching"; title = "Searching…"; }
    else if (st?.startsWith("failed:")) { cls = "failed"; title = st.slice(7); }
    else if (item.subtitles?.[lang]) { cls = "have"; title = `${lang} subtitle present — click to replace`; }
    if (sources) title += "\n" + sources.map((x) => `${x.provider}: ${x.detail}`).join("\n");
    return `<span class="lang-chip ${cls}" data-item="${item.id}" data-lang="${esc(lang)}" title="${esc(title)}">${esc(lang)}</span>`;
  }).join("");

  const isEpisode = !!item.parsed?.isEpisode;
  return `
    <tr data-row="${item.id}">
      <td class="col-langs">${chips}</td>
      <td title="${esc(item.path)}">${esc(item.fileName)}</td>
      <td class="col-parsed ${isEpisode ? "det-cell" : ""}" ${isEpisode ? `data-detect="${item.id}" title="Click to change the show or season"` : ""}>
        <span class="parsed-tag">${detectionHtml(item)}</span></td>
      <td class="col-size">${formatSize(item.sizeBytes)}</td>
      <td class="col-actions">
        <button class="icon-btn" data-reveal="${item.id}" title="Show in folder">&#128194;</button>
        <button class="icon-btn" data-remove="${item.id}" title="Remove from list">&#10005;</button>
      </td>
    </tr>`;
}

function renderFilesRow(itemId) {
  if (state.view !== "files") return;
  const row = document.querySelector(`tr[data-row="${itemId}"]`);
  const item = state.items.find((i) => i.id === itemId);
  if (!row || !item) return;
  const temp = document.createElement("tbody");
  temp.innerHTML = renderFileRowHtml(item);
  const fresh = temp.firstElementChild;
  row.replaceWith(fresh);
  bindFileRowActions(fresh);
}

function bindFileRowActions(root) {
  root.querySelectorAll("[data-remove]").forEach((btn) => {
    btn.onclick = () => del(`/api/library/${btn.dataset.remove}`).catch((e) => toast(e.message, "err"));
  });
  root.querySelectorAll("[data-reveal]").forEach((btn) => {
    btn.onclick = () => post("/api/shell/reveal", { itemId: btn.dataset.reveal }).catch(() => {});
  });
  root.querySelectorAll(".lang-chip").forEach((chip) => {
    chip.onclick = () => openCandidatesModal(chip.dataset.item, chip.dataset.lang);
  });
  root.querySelectorAll("[data-detect]").forEach((cell) => {
    cell.onclick = () => openDetectionModal(cell.dataset.detect);
  });
}

// ---------- Candidates modal ----------------------------------------------------------------

// Guards against a stale fetch writing into a newer modal (close + reopen races).
let modalToken = 0;

async function openCandidatesModal(itemId, language) {
  const item = state.items.find((i) => i.id === itemId);
  if (!item) return;
  const token = ++modalToken;
  const root = document.getElementById("modal-root");
  root.innerHTML = `
    <div class="modal-backdrop">
      <div class="modal">
        <div class="modal-head">
          <span class="title">${esc(item.fileName)} — ${esc(language)}</span>
          <button class="icon-btn" id="modal-close" title="Close">&#10005;</button>
        </div>
        <div class="modal-body"><div class="modal-empty"><span class="spin"></span> Searching providers…</div></div>
      </div>
    </div>`;
  const close = () => { modalToken++; root.innerHTML = ""; };
  root.querySelector("#modal-close").onclick = close;
  root.querySelector(".modal-backdrop").onclick = (e) => { if (e.target.classList.contains("modal-backdrop")) close(); };

  let candidates, sources;
  try {
    ({ candidates, sources } = await get(`/api/subtitles/candidates?itemId=${encodeURIComponent(itemId)}&language=${encodeURIComponent(language)}`));
  } catch (err) {
    if (token !== modalToken) return; // modal was closed/replaced meanwhile
    const body = root.querySelector(".modal-body");
    if (body) body.innerHTML = `<div class="modal-empty">Search failed: ${esc(err.message)}</div>`;
    return;
  }

  if (token !== modalToken) return; // modal was closed/replaced meanwhile
  const body = root.querySelector(".modal-body");
  if (!body) return;

  // What was tried earlier in the batch (downloads, quotas) is more telling than this fresh
  // search alone, so the batch outcome wins when there is one for this file + language.
  const batchSources = state.sources[itemId]?.[language];
  const sourcesHtml = `
    <div class="sources-panel">
      <div class="sources-title">${batchSources ? "Last search" : "Sources"}</div>
      ${(batchSources || sources || []).map((x) => `
        <div class="source-row ${sourceClass(x.status)}">
          <span class="source-name">${esc(x.provider)}</span>
          <span class="source-detail">${esc(x.detail || x.status)}</span>
        </div>`).join("")}
    </div>`;

  if (!candidates.length) {
    body.innerHTML = `${sourcesHtml}<div class="modal-empty">No subtitles found for this file in “${esc(language)}”.</div>`;
    return;
  }

  body.innerHTML = `
    ${sourcesHtml}
    <table class="cand-table">
      <thead><tr><th>Score</th><th>Release</th><th>Provider</th><th>Downloads</th><th></th></tr></thead>
      <tbody>
        ${candidates.map((c, i) => `
          <tr>
            <td>${c.score}${c.hashMatch ? ' <span class="hash-badge">exact match</span>' : ""}</td>
            <td class="release" title="${esc(c.releaseName)}">${esc(c.releaseName) || "<i>unnamed</i>"}${c.hearingImpaired ? ' <span class="hi-badge" title="Hearing impaired (sound cues)">HI</span>' : ""}</td>
            <td>${esc(c.provider)}</td>
            <td>${c.downloads || ""}</td>
            <td><button class="btn small" data-download="${i}">Download</button></td>
          </tr>`).join("")}
      </tbody>
    </table>`;

  body.querySelectorAll("[data-download]").forEach((btn) => {
    btn.onclick = async () => {
      btn.disabled = true;
      btn.textContent = "…";
      try {
        const outcome = await post("/api/subtitles/download", { itemId, candidate: candidates[+btn.dataset.download] });
        if (outcome.downloaded) {
          // Clear any stale "failed" marker from a previous batch for this chip.
          if (state.langState[itemId]) delete state.langState[itemId][language];
          toast(`Subtitle saved next to the video (${esc(language)})`, "ok");
          close();
        } else {
          toast(outcome.error || "Download failed", "err");
          btn.disabled = false;
          btn.textContent = "Download";
        }
      } catch (err) {
        toast(err.message, "err");
        btn.disabled = false;
        btn.textContent = "Download";
      }
    };
  });
}

// ---------- Detection (which show / which season) ----------------------------------------------

function episodeTag(item) {
  const p = item.parsed || {};
  const e = item.effective || {};
  if (e.season != null && e.episode != null) {
    return `S${String(e.season).padStart(2, "0")}E${String(e.episode).padStart(2, "0")}${e.endEpisode ? "-E" + String(e.endEpisode).padStart(2, "0") : ""}`;
  }
  if (p.absoluteEpisode != null) return `#${p.absoluteEpisode}`;
  if (p.year) return `(${p.year})`;
  return "";
}

// What the Detected column says: the show as detected, the season/episode as guessed, and
// whether something needs the user's attention.
function detectionHtml(item) {
  const p = item.parsed || {};
  const d = item.detection;
  const tag = episodeTag(item);
  if (!p.isEpisode) return `${esc(p.title || "")} ${tag ? `<span class="ep">${esc(tag)}</span>` : ""}`;

  let name = esc(p.title || "");
  let extra = "";
  if (!d) extra = `<span class="det-note"><span class="spin"></span> detecting…</span>`;
  else if (d.status === "resolved") name = `<b>${esc(d.show.name)}</b>`;
  else if (d.status === "ambiguous") extra = `<span class="det-warn">which show? click to choose</span>`;
  else extra = `<span class="det-warn">show not found — click to pick</span>`;

  let badge = "";
  if (item.guess && item.guess.confidence !== "explicit")
    badge = `<span class="guess-badge ${item.guess.confidence}" title="${esc(item.guess.reason)}">guessed</span>`;
  else if (d && d.status === "resolved" && !item.guess && p.absoluteEpisode != null)
    badge = `<span class="guess-badge low" title="Episode ${p.absoluteEpisode} doesn't fit any season — click to pick the season">no season</span>`;

  return `${name} <span class="ep">${esc(tag)}</span> ${badge} ${extra}`;
}

function groupKeyOf(item) {
  return (item.parsed?.title || "").trim().toLowerCase();
}

function itemsOfTitle(title) {
  const key = title.trim().toLowerCase();
  return state.items.filter((i) => i.parsed?.isEpisode && groupKeyOf(i) === key);
}

async function chooseShow(title, show) {
  try {
    await post("/api/library/show", { title, show });
    await loadLibrary();
  } catch (err) {
    toast(`Could not set the show: ${err.message}`, "err");
  }
}

async function setSeason(title, season) {
  const itemIds = itemsOfTitle(title).map((i) => i.id);
  try {
    await post("/api/library/season", { itemIds, season: season || null });
    await loadLibrary();
  } catch (err) {
    toast(`Could not set the season: ${err.message}`, "err");
  }
}

function showResultHtml(show, selected) {
  return `
    <div class="show-result ${selected ? "selected" : ""}" data-show='${esc(JSON.stringify(show))}'>
      ${show.imageUrl ? `<img src="${esc(show.imageUrl)}" alt="">` : `<div class="img-placeholder"></div>`}
      <div class="meta">
        <div class="name">${esc(show.name)}</div>
        <div class="sub">${esc([show.premiered?.slice(0, 4), show.network, show.source].filter(Boolean).join(" · "))}</div>
      </div>
    </div>`;
}

function isSameShow(a, b) {
  return !!a && !!b && a.id === b.id && a.source === b.source;
}

function seasonSelectHtml(group, current) {
  const seasons = group[0]?.detection?.seasons?.length ? group[0].detection.seasons : Array.from({ length: 12 }, (_, i) => i + 1);
  return `
    <select class="input season-select" title="Season for files whose name carries only an episode number">
      <option value="">Season: auto</option>
      ${seasons.map((s) => `<option value="${s}" ${current === s ? "selected" : ""}>Season ${s}</option>`).join("")}
    </select>`;
}

// Binds the show list + search box + season select inside `root` for one title group.
function bindShowPicker(root, title, onChanged) {
  root.querySelectorAll("[data-show]").forEach((el) => {
    el.onclick = async () => {
      await chooseShow(title, JSON.parse(el.dataset.show));
      onChanged?.();
    };
  });
  const select = root.querySelector(".season-select");
  if (select) select.onchange = async () => { await setSeason(title, Number(select.value)); onChanged?.(); };

  const input = root.querySelector(".show-search");
  const results = root.querySelector(".show-search-results");
  if (input && results) {
    input.oninput = debounce(async () => {
      const q = input.value.trim();
      if (q.length < 2) { results.innerHTML = ""; return; }
      let found = [];
      try { found = await get(`/api/rename/shows?q=${encodeURIComponent(q)}`); } catch { /* ignore */ }
      results.innerHTML = found.length
        ? found.map((s) => showResultHtml(s, false)).join("")
        : `<div class="hint-text" style="padding:6px 2px">No results.</div>`;
      results.querySelectorAll("[data-show]").forEach((el) => {
        el.onclick = async () => { await chooseShow(title, JSON.parse(el.dataset.show)); onChanged?.(); };
      });
    }, 400);
  }
}

let detectionModalToken = 0;

function openDetectionModal(itemId) {
  const item = state.items.find((i) => i.id === itemId);
  if (!item || !item.parsed?.isEpisode) return;
  const title = item.parsed.title;
  const token = ++detectionModalToken;
  const root = document.getElementById("modal-root");
  const close = () => { detectionModalToken++; root.innerHTML = ""; };

  const draw = () => {
    if (token !== detectionModalToken) return;
    const group = itemsOfTitle(title);
    const d = group[0]?.detection;
    const current = d?.show || null;
    const candidates = d?.candidates || [];
    const override = group.find((i) => i.seasonOverride != null)?.seasonOverride ?? null;
    root.innerHTML = `
      <div class="modal-backdrop">
        <div class="modal">
          <div class="modal-head">
            <span class="title">“${esc(title)}” — ${group.length} file${group.length === 1 ? "" : "s"}</span>
            <button class="icon-btn" id="modal-close" title="Close">&#10005;</button>
          </div>
          <div class="modal-body detect-body">
            <div class="detect-section">
              <div class="detect-label">Show</div>
              ${!d ? `<div class="hint-text"><span class="spin"></span> Detecting…</div>`
                : d.status === "ambiguous" ? `<div class="warn-text" style="margin-bottom:6px">Several shows match — pick the right one:</div>`
                : d.status === "notFound" ? `<div class="warn-text" style="margin-bottom:6px">No show matches this name. Pick one below or search for it.</div>`
                : ""}
              ${candidates.map((s) => showResultHtml(s, isSameShow(s, current))).join("")}
              <input class="input show-search" placeholder="Search another show on TVMaze / TMDB…" style="margin-top:8px">
              <div class="show-search-results"></div>
            </div>
            <div class="detect-section">
              <div class="detect-label">Season</div>
              <div class="hint-text" style="margin-bottom:6px">For files whose name carries only an episode number (e.g. “- 15”). Files that spell out their season keep it.</div>
              ${seasonSelectHtml(group, override)}
            </div>
            <div class="detect-section">
              <div class="detect-label">Files</div>
              ${group.map((i) => `<div class="detect-file"><span class="ep">${esc(episodeTag(i))}</span> ${esc(i.fileName)}${i.guess && i.guess.confidence !== "explicit" ? ` <span class="hint-text">— ${esc(i.guess.reason)}</span>` : ""}</div>`).join("")}
            </div>
          </div>
        </div>
      </div>`;
    root.querySelector("#modal-close").onclick = close;
    root.querySelector(".modal-backdrop").onclick = (e) => { if (e.target.classList.contains("modal-backdrop")) close(); };
    bindShowPicker(root, title, draw);
  };
  draw();
}

// ---------- Rename view -------------------------------------------------------------------------

// Guards against out-of-order proposal responses.
let proposalsToken = 0;

async function loadProposals() {
  const token = ++proposalsToken;
  try {
    const proposals = await post("/api/rename/proposals", {});
    if (token !== proposalsToken) return;
    state.proposals = proposals;
    for (const proposal of proposals)
      if (!(proposal.itemId in state.proposalChecks)) state.proposalChecks[proposal.itemId] = proposal.canApply;
  } catch (err) {
    if (token !== proposalsToken) return;
    state.proposals = [];
    toast(`Could not build the proposals: ${err.message}`, "err");
  }
  if (state.view === "rename") render();
}

function renameGroups() {
  const groups = new Map();
  for (const item of state.items) {
    const d = item.detection;
    const key = !item.parsed?.isEpisode ? "movies"
      : d?.show ? `show:${d.show.source}:${d.show.id}`
      : `title:${groupKeyOf(item)}`;
    if (!groups.has(key)) groups.set(key, { key, items: [] });
    groups.get(key).items.push(item);
  }
  return [...groups.values()].sort((a, b) => (a.key === "movies") - (b.key === "movies"));
}

function renderRenameView(main) {
  if (!state.items.length) {
    main.innerHTML = `<div class="dropzone"><div class="title">Nothing to rename yet</div><div>Add some episodes in the Files tab first.</div></div>`;
    return;
  }
  if (state.proposals === null) {
    main.innerHTML = `<div class="panel-empty"><span class="spin"></span> Detecting shows and matching episodes…</div>`;
    loadProposals();
    return;
  }

  const byItem = Object.fromEntries(state.proposals.map((p) => [p.itemId, p]));
  const applicable = state.proposals.filter((p) => state.proposalChecks[p.itemId] && p.canApply);
  main.innerHTML = `
    <div class="actionbar">
      <span class="hint-text">Shows are detected from the filenames — the same way the subtitle search sees them. Pick a show or a season where the guess is wrong.</span>
      <div class="grow"></div>
      <button class="btn secondary" id="redetect" ${state.renameBusy ? "disabled" : ""}>Detect again</button>
      <button class="btn" id="apply-renames" ${applicable.length && !state.renameBusy ? "" : "disabled"}>
        ${state.renameBusy ? "Renaming…" : `Rename ${applicable.length} file${applicable.length === 1 ? "" : "s"}`}
      </button>
    </div>
    ${renameGroups().map((group) => renderRenameGroupHtml(group, byItem)).join("")}`;

  main.querySelectorAll(".rename-group").forEach((card) => {
    const title = card.dataset.title;
    if (title) bindShowPicker(card, title, () => loadProposals());
    card.querySelectorAll("[data-change]").forEach((btn) => { btn.onclick = () => openDetectionModal(btn.dataset.change); });
  });

  main.querySelectorAll("[data-check]").forEach((box) => {
    box.onchange = () => {
      state.proposalChecks[box.dataset.check] = box.checked;
      const count = state.proposals.filter((p) => state.proposalChecks[p.itemId] && p.canApply).length;
      const btn = main.querySelector("#apply-renames");
      btn.disabled = count === 0 || state.renameBusy;
      btn.textContent = `Rename ${count} file${count === 1 ? "" : "s"}`;
    };
  });

  main.querySelector("#redetect").onclick = async () => {
    state.renameBusy = true;
    render();
    try { await post("/api/library/detect"); await loadLibrary(); }
    catch (err) { toast(err.message, "err"); }
    finally { state.renameBusy = false; await loadProposals(); }
  };

  main.querySelector("#apply-renames").onclick = async () => {
    const chosen = state.proposals.filter((p) => state.proposalChecks[p.itemId] && p.canApply);
    if (!chosen.length) return;
    state.renameBusy = true;
    render();
    try {
      const outcomes = await post("/api/rename/apply", { proposals: chosen });
      const ok = outcomes.filter((o) => o.renamed).length;
      const failed = outcomes.filter((o) => !o.renamed);
      const warned = outcomes.find((o) => o.renamed && o.error);
      toast(`Renamed ${ok} file${ok === 1 ? "" : "s"}${failed.length ? `, ${failed.length} failed` : ""}`,
        failed.length ? "err" : "ok", 6000);
      if (warned) toast(warned.error, "info", 8000);
      await loadLibrary();
    } catch (err) {
      toast(err.message, "err");
    } finally {
      state.renameBusy = false;
      await loadProposals();
    }
  };
}

function renderRenameGroupHtml(group, byItem) {
  const first = group.items[0];
  const d = first.detection;
  const title = first.parsed?.title || "";
  const override = group.items.find((i) => i.seasonOverride != null)?.seasonOverride ?? null;

  let head;
  if (group.key === "movies") {
    head = `<div class="panel-title">Not TV episodes</div>
      <div class="hint-text" style="padding:0 14px 10px">Movies and files without an episode number are left alone.</div>`;
  } else if (!d) {
    head = `<div class="panel-title">“${esc(title)}”</div>
      <div class="hint-text" style="padding:0 14px 10px"><span class="spin"></span> Detecting the show…</div>`;
  } else if (d.status === "resolved") {
    const s = d.show;
    head = `
      <div class="group-head">
        ${s.imageUrl ? `<img src="${esc(s.imageUrl)}" alt="">` : `<div class="img-placeholder"></div>`}
        <div class="meta">
          <div class="name">${esc(s.name)}</div>
          <div class="sub">${esc([s.premiered?.slice(0, 4), s.network, s.source, d.chosenByUser ? "chosen by you" : `detected from “${title}”`].filter(Boolean).join(" · "))}</div>
        </div>
        ${seasonSelectHtml(group.items, override)}
        <button class="btn small secondary" data-change="${esc(first.id)}">Change show…</button>
      </div>`;
  } else {
    const why = d.status === "ambiguous"
      ? `Several shows match “${esc(title)}” — which one is it?`
      : `No show found for “${esc(title)}” — pick one or search for it:`;
    head = `
      <div class="group-head wrap">
        <div class="warn-text" style="flex-basis:100%">${why}</div>
        <div class="show-candidates">${d.candidates.map((s) => showResultHtml(s, false)).join("")}</div>
        <div style="flex-basis:100%">
          <input class="input show-search" placeholder="Search on TVMaze / TMDB…" style="max-width:360px">
          <div class="show-search-results"></div>
        </div>
      </div>`;
  }

  return `
    <div class="card rename-group" data-title="${group.key === "movies" ? "" : esc(title)}">
      ${head}
      <table class="rename-table">
        <thead><tr><th class="col-check"></th><th>Current name</th><th class="arrow"></th><th>New name</th></tr></thead>
        <tbody>
          ${group.items.map((item) => {
            const p = byItem[item.id];
            if (!p) return "";
            return `
              <tr class="${p.problem ? "problem" : ""}">
                <td class="col-check"><input type="checkbox" data-check="${esc(p.itemId)}"
                  ${state.proposalChecks[p.itemId] && p.canApply ? "checked" : ""} ${p.canApply ? "" : "disabled"}></td>
                <td title="${esc(p.oldName)}">${esc(p.oldName)}</td>
                <td class="arrow">&#8594;</td>
                <td class="new-name" title="${esc(p.problem || p.note || p.newName)}">
                  ${p.problem ? `<span class="rename-problem">${esc(p.problem)}</span>` : esc(p.newName)}
                  ${p.note ? `<span class="guess-badge ${item.guess?.confidence === "low" ? "low" : ""}" title="${esc(p.note)}">guessed</span>` : ""}
                </td>
              </tr>`;
          }).join("")}
        </tbody>
      </table>
    </div>`;
}

// ---------- Shows view --------------------------------------------------------------------------

// Guards against out-of-order responses when switching shows quickly.
let showsToken = 0;

async function loadShows() {
  const token = ++showsToken;
  try {
    const shows = await get("/api/shows");
    if (token !== showsToken) return;
    state.shows = shows;
    // Keep the selection if its show is still there, else pick the first one.
    const key = (s) => `${s.show.source}:${s.show.id}`;
    if (!state.selectedShow || !shows.some((s) => key(s) === state.selectedShow)) {
      state.selectedShow = shows[0] ? key(shows[0]) : null;
      state.showDetails = null;
    }
  } catch (err) {
    if (token !== showsToken) return;
    state.shows = [];
    toast(`Could not list the shows: ${err.message}`, "err");
  }
  if (state.view === "shows") render();
  if (state.selectedShow && !state.showDetails) loadShowDetails();
}

async function loadShowDetails() {
  const key = state.selectedShow;
  if (!key) return;
  const [source, id] = key.split(":");
  try {
    const details = await get(`/api/shows/${encodeURIComponent(source)}/${encodeURIComponent(id)}`);
    if (state.selectedShow !== key) return;
    state.showDetails = details;
  } catch (err) {
    if (state.selectedShow !== key) return;
    state.showDetails = { error: err.message };
  }
  if (state.view === "shows") renderShowDetails();
}

const loadShowsDebounced = debounce(() => loadShows().catch(console.error), 400);

function renderShowsView(main) {
  if (!state.items.length) {
    main.innerHTML = `<div class="dropzone"><div class="title">No shows yet</div><div>Add some episodes in the Files tab first.</div></div>`;
    return;
  }
  if (state.shows === null) {
    main.innerHTML = `<div class="panel-empty"><span class="spin"></span> Detecting shows…</div>`;
    loadShows();
    return;
  }
  if (!state.shows.length) {
    main.innerHTML = `<div class="dropzone"><div class="title">No show recognised</div><div>Pick the show for your files in the Files or Rename tab, and it shows up here.</div></div>`;
    return;
  }

  main.innerHTML = `
    <div class="shows-grid">
      <div class="card shows-list">
        ${state.shows.map((s) => {
          const key = `${s.show.source}:${s.show.id}`;
          return `
            <div class="show-result show-entry ${state.selectedShow === key ? "selected" : ""}" data-key="${esc(key)}">
              ${s.show.imageUrl ? `<img src="${esc(s.show.imageUrl)}" alt="">` : `<div class="img-placeholder"></div>`}
              <div class="meta">
                <div class="name">${esc(s.show.name)}</div>
                <div class="sub">${s.have}/${s.aired} episodes${s.missing ? ` · <span class="missing">${s.missing} missing</span>` : " · complete"}</div>
              </div>
            </div>`;
        }).join("")}
      </div>
      <div class="card" id="show-details"></div>
    </div>`;

  main.querySelectorAll("[data-key]").forEach((el) => {
    el.onclick = () => {
      if (state.selectedShow === el.dataset.key) return;
      state.selectedShow = el.dataset.key;
      state.showDetails = null;
      render();
      loadShowDetails();
    };
  });
  renderShowDetails();
}

function fmtDate(iso) {
  if (!iso) return "";
  const d = new Date(iso + (iso.length === 10 ? "T00:00:00" : ""));
  return isNaN(d) ? iso : d.toLocaleDateString(undefined, { year: "numeric", month: "short", day: "numeric" });
}

function renderShowDetails() {
  const panel = document.getElementById("show-details");
  if (!panel) return;
  const d = state.showDetails;
  if (!d) {
    panel.innerHTML = `<div class="panel-empty"><span class="spin"></span> Loading show…</div>`;
    return;
  }
  if (d.error) {
    panel.innerHTML = `<div class="panel-empty warn-text">${esc(d.error)}</div>`;
    return;
  }

  const info = d.info || {};
  const facts = [
    info.premiered ? fmtDate(info.premiered).slice(-4) + (info.ended ? `–${fmtDate(info.ended).slice(-4)}` : "") : null,
    info.status,
    info.network || d.show.network,
    info.rating ? `★ ${info.rating}` : null,
    d.show.source,
  ].filter(Boolean);

  const missingSeasons = d.seasons.filter((s) => s.missing > 0);
  const nextUnaired = d.seasons.flatMap((s) => s.episodes).find((e) => !e.aired);

  panel.innerHTML = `
    <div class="show-head">
      ${info.imageUrl || d.show.imageUrl ? `<img class="poster" src="${esc(info.imageUrl || d.show.imageUrl)}" alt="">` : `<div class="poster img-placeholder"></div>`}
      <div class="show-head-meta">
        <h2>${esc(d.show.name)}</h2>
        <div class="facts">${esc(facts.join(" · "))}</div>
        ${info.genres?.length ? `<div class="genres">${info.genres.map((g) => `<span class="genre">${esc(g)}</span>`).join("")}</div>` : ""}
        ${info.summary ? `<p class="summary">${esc(info.summary)}</p>` : `<p class="summary hint-text">No summary available.</p>`}
        <div class="show-actions">
          ${info.trailerUrl ? `<button class="btn small" data-open="${esc(info.trailerUrl)}">▶ Watch trailer</button>` : `<span class="hint-text">No trailer listed${state.settings?.tmdb?.enabled ? "" : " — enable TMDB in Settings for trailers"}</span>`}
          ${info.officialSite ? `<button class="btn small secondary" data-open="${esc(info.officialSite)}">Official site</button>` : ""}
        </div>
        <div class="coverage-line">
          <b>${d.have}</b> of <b>${d.aired}</b> aired episodes linked
          ${d.missing ? ` · <span class="missing">${d.missing} missing</span>` : " · complete"}
          ${d.unaired ? ` · ${d.unaired} not aired yet${nextUnaired?.airdate ? ` (next: ${esc(fmtDate(nextUnaired.airdate))})` : ""}` : ""}
          · ${d.files} file${d.files === 1 ? "" : "s"}
        </div>
      </div>
    </div>

    <div class="show-section">
      <div class="panel-title">Missing episodes</div>
      ${missingSeasons.length === 0
        ? `<div class="hint-text" style="padding:0 14px 12px">Every aired episode has a file. 🎉</div>`
        : missingSeasons.map((s) => `
          <div class="missing-season">
            <div class="missing-season-head">Season ${s.season} — ${s.missing} of ${s.aired} missing</div>
            <div class="missing-list">
              ${s.episodes.filter((e) => e.aired && !e.have).map((e) => `
                <span class="missing-ep" title="${esc(e.name)}${e.airdate ? ` · ${fmtDate(e.airdate)}` : ""}">S${String(e.season).padStart(2, "0")}E${String(e.number).padStart(2, "0")}</span>`).join("")}
            </div>
          </div>`).join("")}
    </div>

    <div class="show-section">
      <div class="panel-title">Viewing order</div>
      ${d.seasons.map((s) => `
        <details class="season" ${s.missing > 0 || d.seasons.length === 1 ? "open" : ""}>
          <summary>Season ${s.season} <span class="hint-text">· ${s.have}/${s.aired} linked${s.unaired ? ` · ${s.unaired} upcoming` : ""}</span></summary>
          <table class="episode-table">
            ${s.episodes.map((e) => `
              <tr class="${e.have ? "have" : e.aired ? "missing" : "unaired"}">
                <td class="ep">S${String(e.season).padStart(2, "0")}E${String(e.number).padStart(2, "0")}</td>
                <td class="ep-name" title="${esc(e.name)}">${esc(e.name) || "<i>untitled</i>"}</td>
                <td class="ep-date">${esc(fmtDate(e.airdate))}</td>
                <td class="ep-file" title="${esc(e.fileName || "")}">${e.have ? "✓ " + esc(e.fileName) : e.aired ? "missing" : "not aired yet"}</td>
              </tr>`).join("")}
          </table>
        </details>`).join("")}
    </div>`;

  panel.querySelectorAll("[data-open]").forEach((btn) => {
    btn.onclick = () => post("/api/shell/open", { url: btn.dataset.open }).catch((e) => toast(e.message, "err"));
  });
}

// ---------- Settings view --------------------------------------------------------------------------

// Settings save themselves: toggles and checkboxes right away, text fields shortly after typing stops.
let settingsSaveTimer = null;
let settingsSaveToken = 0;

function renderSettingsView(main) {
  const s = state.settings;
  if (!s) { main.innerHTML = ""; return; }
  const selected = new Set(s.languages);
  const u = state.update || {};

  main.innerHTML = `
    <div class="settings-page card">
      <div class="settings-section settings-autosave">
        <span id="settings-saved" class="hint-text">Changes are saved automatically.</span>
      </div>

      <div class="settings-section">
        <h3>Subtitle languages</h3>
        <div class="desc">Subtitles are fetched for every selected language and saved as <code>video.&lt;lang&gt;.srt</code>.</div>
        <div class="lang-grid">
          ${state.meta.languages.map((l) => `
            <label class="lang-option">
              <input type="checkbox" data-lang="${l.code}" ${selected.has(l.code) ? "checked" : ""}>
              <span>${esc(l.name)} <span style="color:var(--dim)">(${l.code})</span></span>
            </label>`).join("")}
        </div>
      </div>

      <div class="settings-section">
        <h3>Providers</h3>
        <div class="desc">Searches run on every enabled provider; the best-scored subtitle wins. Each provider's status is shown here and in the Files tab.</div>

        <div class="provider-card ${s.gestdown.enabled ? "enabled" : ""}" id="card-gestdown">
          <div class="head">
            <label class="toggle"><input type="checkbox" id="gestdown-enabled" ${s.gestdown.enabled ? "checked" : ""}><span class="slider"></span></label>
            <span class="name">Gestdown</span>
            <span class="badge">no key needed</span>
            <span class="note">TV shows (Addic7ed mirror)</span>
            <span class="provider-status" data-status="Gestdown"></span>
          </div>
        </div>

        <div class="provider-card ${s.openSubtitles.enabled ? "enabled" : ""}" id="card-opensubtitles">
          <div class="head">
            <label class="toggle"><input type="checkbox" id="os-enabled" ${s.openSubtitles.enabled ? "checked" : ""}><span class="slider"></span></label>
            <span class="name">OpenSubtitles</span>
            <span class="note">movies + TV — <a href="https://www.opensubtitles.com/en/consumers" target="_blank">get a free API key</a></span>
            <span class="provider-status" data-status="OpenSubtitles"></span>
          </div>
          <div class="fields">
            <div class="settings-row"><label>API key</label><input class="input" id="os-key" value="${esc(s.openSubtitles.apiKey)}" placeholder="${state.meta?.builtInKeys?.openSubtitles ? "Using built-in key — paste yours to override" : "Required"}"></div>
            <div class="settings-row"><label>Username</label><input class="input" id="os-user" value="${esc(s.openSubtitles.username)}" placeholder="Optional — your OpenSubtitles username (not your e-mail)"></div>
            <div class="settings-row"><label>Password</label><input class="input" id="os-pass" type="password" value="${esc(s.openSubtitles.password)}" placeholder="Optional"></div>
            <div class="settings-row">
              <label></label>
              <button class="btn small secondary" id="os-test-login">Test login</button>
              <span id="os-login-status" class="hint-text"></span>
            </div>
          </div>
        </div>

        <div class="provider-card ${s.subDl.enabled ? "enabled" : ""}" id="card-subdl">
          <div class="head">
            <label class="toggle"><input type="checkbox" id="subdl-enabled" ${s.subDl.enabled ? "checked" : ""}><span class="slider"></span></label>
            <span class="name">SubDL</span>
            <span class="note">movies + TV — <a href="https://subdl.com/panel/api" target="_blank">get a free API key</a></span>
            <span class="provider-status" data-status="SubDL"></span>
          </div>
          <div class="fields">
            <div class="settings-row"><label>API key</label><input class="input" id="subdl-key" value="${esc(s.subDl.apiKey)}" placeholder="Required"></div>
          </div>
        </div>

        <div class="provider-card ${s.subSource.enabled ? "enabled" : ""}" id="card-subsource">
          <div class="head">
            <label class="toggle"><input type="checkbox" id="subsource-enabled" ${s.subSource.enabled ? "checked" : ""}><span class="slider"></span></label>
            <span class="name">SubSource</span>
            <span class="note">movies + TV (Subscene archive) — <a href="https://subsource.net/profile" target="_blank">get a free API key</a></span>
            <span class="provider-status" data-status="SubSource"></span>
          </div>
          <div class="fields">
            <div class="settings-row"><label>API key</label><input class="input" id="subsource-key" value="${esc(s.subSource.apiKey)}" placeholder="Required"></div>
          </div>
        </div>
      </div>

      <div class="settings-section">
        <h3>Rename sources</h3>
        <div class="desc">TVMaze is always on (no key needed). TMDB fills the gaps when a show or episode is missing on TVMaze.</div>

        <div class="provider-card ${s.tmdb.enabled ? "enabled" : ""}" id="card-tmdb">
          <div class="head">
            <label class="toggle"><input type="checkbox" id="tmdb-enabled" ${s.tmdb.enabled ? "checked" : ""}><span class="slider"></span></label>
            <span class="name">TMDB</span>
            <span class="note"><a href="https://www.themoviedb.org/settings/api" target="_blank">get a free API key</a> (v3 key or v4 token)</span>
          </div>
          <div class="fields">
            <div class="settings-row"><label>API key</label><input class="input" id="tmdb-key" value="${esc(s.tmdb.apiKey)}" placeholder="${state.meta?.builtInKeys?.tmdb ? "Using built-in key — paste yours to override" : "Required"}"></div>
          </div>
        </div>
      </div>

      <div class="settings-section">
        <h3>Downloads</h3>
        <div class="settings-row">
          <label class="toggle"><input type="checkbox" id="overwrite" ${s.overwriteExisting ? "checked" : ""}><span class="slider"></span></label>
          <span>Re-download subtitles that already exist</span>
        </div>
      </div>

      <div class="settings-section">
        <h3>Renaming</h3>
        <div class="desc">Placeholders: <code>{Show}</code>, <code>{Season}</code>, <code>{Episode}</code>, <code>{Title}</code></div>
        <div class="settings-row"><label>Template</label><input class="input" id="rename-template" value="${esc(s.renameTemplate)}" style="max-width:430px"></div>
      </div>

      <div class="settings-section">
        <h3>Updates</h3>
        <div class="desc">Subtitall v${esc(state.meta.version)} — updates come from GitHub Releases and install when you restart the app.</div>
        <div class="settings-row">
          <button class="btn small secondary" id="check-updates" ${u.state === "checking" || u.state === "downloading" ? "disabled" : ""}>Check for updates</button>
          <span id="update-status-text" class="hint-text">${updateStatusText()}</span>
          ${u.state === "downloaded" ? `<button class="btn small" id="install-update">Restart to update</button>` : ""}
        </div>
      </div>
    </div>`;

  ["gestdown", "opensubtitles", "subdl", "subsource", "tmdb"].forEach((key) => {
    const card = main.querySelector(`#card-${key}`);
    const box = card.querySelector("input[type=checkbox]");
    box.onchange = () => card.classList.toggle("enabled", box.checked);
  });

  const collect = () => {
    const languages = [...main.querySelectorAll("[data-lang]")].filter((box) => box.checked).map((box) => box.dataset.lang);
    return {
      ...s,
      languages,
      gestdown: { ...s.gestdown, enabled: main.querySelector("#gestdown-enabled").checked },
      openSubtitles: {
        ...s.openSubtitles,
        enabled: main.querySelector("#os-enabled").checked,
        apiKey: main.querySelector("#os-key").value.trim(),
        username: main.querySelector("#os-user").value.trim(),
        password: main.querySelector("#os-pass").value,
      },
      subDl: { ...s.subDl, enabled: main.querySelector("#subdl-enabled").checked, apiKey: main.querySelector("#subdl-key").value.trim() },
      subSource: { ...s.subSource, enabled: main.querySelector("#subsource-enabled").checked, apiKey: main.querySelector("#subsource-key").value.trim() },
      tmdb: { ...s.tmdb, enabled: main.querySelector("#tmdb-enabled").checked, apiKey: main.querySelector("#tmdb-key").value.trim() },
      overwriteExisting: main.querySelector("#overwrite").checked,
      renameTemplate: main.querySelector("#rename-template").value.trim() || "{Show} - S{Season}E{Episode} - {Title}",
    };
  };

  const status = main.querySelector("#settings-saved");
  const save = async () => {
    const updated = collect();
    if (!updated.languages.length) {
      status.textContent = "Select at least one language.";
      status.className = "warn-text";
      return;
    }
    const token = ++settingsSaveToken;
    status.textContent = "Saving…";
    status.className = "hint-text";
    try {
      const saved = await put("/api/settings", updated);
      if (token !== settingsSaveToken) return;
      state.settings = saved;
      status.textContent = "Saved ✓";
      status.className = "hint-text ok-text";
      loadProviderStatus();
    } catch (err) {
      if (token !== settingsSaveToken) return;
      status.textContent = `Could not save: ${err.message}`;
      status.className = "warn-text";
    }
  };
  const saveSoon = () => { clearTimeout(settingsSaveTimer); settingsSaveTimer = setTimeout(save, 600); };

  main.querySelectorAll("input[type=checkbox]").forEach((box) => box.addEventListener("change", save));
  main.querySelectorAll("input.input").forEach((input) => input.addEventListener("input", saveSoon));

  main.querySelector("#os-test-login").onclick = async () => {
    const btn = main.querySelector("#os-test-login");
    const out = main.querySelector("#os-login-status");
    btn.disabled = true;
    out.textContent = "Logging in…";
    try {
      clearTimeout(settingsSaveTimer);
      await save(); // Make sure the credentials being tested are the saved ones.
      const result = await post("/api/providers/opensubtitles/login");
      out.textContent = loginText(result);
      out.className = result.ok ? "hint-text ok-text" : "warn-text";
      loadProviderStatus();
    } catch (err) {
      out.textContent = err.message;
      out.className = "warn-text";
    } finally {
      btn.disabled = false;
    }
  };

  main.querySelector("#check-updates").onclick = async () => {
    try { state.update = await post("/api/update/check"); } catch (err) { toast(err.message, "err"); }
    renderUpdateStatus();
  };
  const install = main.querySelector("#install-update");
  if (install) install.onclick = () => post("/api/update/install").catch((e) => toast(e.message, "err"));

  renderProviderStatus();
}

function loginText(login) {
  if (!login) return "";
  if (login.ok) return `Logged in as ${login.user}${login.allowedDownloads ? ` — ${login.allowedDownloads} downloads/day` : ""}`;
  return login.error || "Login failed";
}

// ---------- Provider status (quotas, cooldowns, account) ----------------------------------------------

async function loadProviderStatus() {
  try {
    state.providerStatus = await get("/api/providers/status");
  } catch { return; }
  renderProviderStatus();
}

function providerStatusText(p) {
  if (!p.configured) return { text: "off", cls: "" };
  const parts = [];
  if (p.account) {
    if (!p.account.hasCredentials) parts.push("anonymous (~5 downloads/day per IP)");
    else if (p.account.login?.ok) parts.push(`logged in as ${p.account.login.user}`);
    else if (p.account.login) parts.push(`login failed: ${p.account.login.error}`);
    else parts.push("account set — login not tested yet");
  }
  if (p.quota) {
    if (p.quota.exhausted) parts.push(`download quota used up${p.quota.resetAt ? `, resets ${new Date(p.quota.resetAt).toLocaleTimeString([], { hour: "2-digit", minute: "2-digit" })}` : ""}`);
    else if (p.quota.remaining != null) parts.push(`${p.quota.remaining} download${p.quota.remaining === 1 ? "" : "s"} left today`);
  }
  if (p.cooldownUntil && new Date(p.cooldownUntil) > new Date()) parts.push("paused after repeated failures");
  const bad = p.quota?.exhausted || (p.account?.login && !p.account.login.ok) || p.cooldownUntil;
  return { text: parts.join(" · ") || "ready", cls: bad ? "warn" : "ok" };
}

function renderProviderStatus() {
  const list = state.providerStatus || [];
  document.querySelectorAll("[data-status]").forEach((el) => {
    const p = list.find((x) => x.name === el.dataset.status);
    if (!p) return;
    const { text, cls } = providerStatusText(p);
    el.textContent = text;
    el.className = `provider-status ${cls}`;
  });
  const os = list.find((x) => x.name === "OpenSubtitles");
  const out = document.getElementById("os-login-status");
  if (out && os?.account?.login && !out.textContent) {
    out.textContent = loginText(os.account.login);
    out.className = os.account.login.ok ? "hint-text ok-text" : "warn-text";
  }
  const line = document.getElementById("sources-line");
  if (line) line.innerHTML = sourcesLineHtml();
}

// One line for the Files tab: which sources a search will use, and anything holding them back.
function sourcesLineHtml() {
  const list = state.providerStatus || [];
  const on = list.filter((p) => p.configured);
  if (!on.length) return "";
  return "Sources: " + on.map((p) => {
    const { text, cls } = providerStatusText(p);
    return `<span class="source-pill ${cls}" title="${esc(text)}">${esc(p.name)}${cls === "warn" ? " ⚠" : ""}</span>`;
  }).join(" ");
}

// ---------- Update status -----------------------------------------------------------------------------

function updateStatusText() {
  const u = state.update;
  if (!u) return "";
  return u.message || "";
}

function renderUpdateStatus() {
  const u = state.update || {};
  const top = document.getElementById("top-update");
  if (top) {
    const busy = u.state === "checking" || u.state === "downloading" || u.state === "available";
    top.hidden = !(busy || u.state === "downloaded");
    if (u.state === "downloaded") {
      top.innerHTML = `<button class="btn small" id="top-install-update" title="${esc(u.message || "")}">Restart to update to v${esc(u.version || "")}</button>`;
      top.querySelector("#top-install-update").onclick = () => post("/api/update/install").catch((e) => toast(e.message, "err"));
    } else if (busy) {
      const pct = u.state === "downloading" && u.percent != null ? ` ${Math.round(u.percent)}%` : "";
      top.innerHTML = `<span class="spin"></span> ${esc(u.state === "checking" ? "Checking for updates…" : `Downloading update${u.version ? ` v${u.version}` : ""}…${pct}`)}`;
    }
  }
  const text = document.getElementById("update-status-text");
  if (text) {
    text.textContent = updateStatusText();
    const btn = document.getElementById("check-updates");
    if (btn) btn.disabled = u.state === "checking" || u.state === "downloading";
    if (u.state === "downloaded" && !document.getElementById("install-update") && state.view === "settings") render();
  }
}

// ---------- Init ------------------------------------------------------------------------------------

function setupNavigation() {
  document.querySelectorAll(".tab").forEach((tab) => {
    tab.onclick = () => { state.view = tab.dataset.view; render(); };
  });
}

(async function init() {
  setupNavigation();
  setupDragDrop();
  try {
    await loadAll();
  } catch (err) {
    toast(`Failed to load: ${err.message}`, "err");
  }
  connectEvents();
  render();
})();
