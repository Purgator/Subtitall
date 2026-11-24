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
  // re-encode view
  reencode: {
    ffmpeg: null,         // what /api/reencode/status says about ffmpeg (null = not asked yet)
    jobs: {},             // itemId -> job (queued, encoding, checking, done, failed)
    running: false,
    trial: null,          // the trial on short excerpts, running or finished
    files: null,          // itemId -> { info, advice } (null = not analysed yet)
    checks: {},           // itemId -> ticked for re-encoding
    tested: {},           // group key -> quality level chosen after a trial
    showOptions: false,
    showOwn: false,
    statusError: null,
  },
  // sync view
  sync: {
    itemId: null,         // the video opened in the list
    all: null,            // itemId -> /api/sync/{itemId}: its subtitles and what the comparison found (null = not loaded yet)
    error: null,
    open: {},             // subtitle path -> details shown
    started: {},          // itemId -> a comparison was started automatically on this visit
    busy: {},             // itemId -> { percent, step } while a comparison runs
  },
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
    // Same for the update check: its answer may have gone out before this stream was open,
    // which would leave "Checking for updates…" spinning although the check is long over.
    try {
      state.update = await get("/api/update/status");
      renderUpdateStatus();
    } catch { /* backend not ready yet */ }
    // And for the encodes: one may have finished while the stream was down.
    try {
      await loadReencodeStatus();
      if (state.view === "reencode") render(); else renderTopbar();
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
        // Proposals follow the library: refresh them when they are on screen, and forget them
        // otherwise so the Rename tab never opens on names computed for an older file list.
        if (state.view !== "rename") state.proposals = null;
        else if (state.proposals !== null && !state.renameBusy) loadProposalsDebounced();
        if (state.shows !== null) { state.showDetails = null; loadShowsDebounced(); }
        // And for what the Sync tab knows about each video's subtitles.
        if (type === "library-changed") {
          if (state.view === "sync") loadSyncAllDebounced();
          else if (!Object.keys(state.sync.busy).length) state.sync.all = null;
        }
        // Same for what the Re-encode tab knows about each file.
        if (type === "library-changed" && state.reencode.files !== null) {
          if (state.view === "reencode") loadReencodeFilesDebounced();
          else state.reencode.files = null;
        }
        break;
      case "ffmpeg-status": {
        const was = state.reencode.ffmpeg?.state;
        state.reencode.ffmpeg = data;
        if (state.view === "reencode" || state.view === "sync") render();
        if (data.state === "ready" && was && was !== "ready") toast(`ffmpeg ${data.version} is installed and ready`, "ok", 6000);
        break;
      }
      case "sync-changed":
        if (data.state === "running" || data.state === "queued") state.sync.busy[data.itemId] = { percent: data.percent, step: data.step };
        else delete state.sync.busy[data.itemId];
        if (state.sync.all) state.sync.all[data.itemId] = data;
        if (state.view === "sync") render(); else renderTopbar();
        break;
      case "sync-progress": {
        state.sync.busy[data.itemId] = { percent: data.percent, step: data.step };
        if (state.sync.all?.[data.itemId]) Object.assign(state.sync.all[data.itemId], { percent: data.percent, step: data.step });
        renderTopbar();
        if (state.view === "sync") {
          const set = (selector, apply) => { const el = document.querySelector(selector); if (el) apply(el); };
          set(`[data-sync-bar="${data.itemId}"]`, (el) => { el.style.width = `${data.percent}%`; });
          set(`[data-sync-step="${data.itemId}"]`, (el) => { el.textContent = data.step; });
          if (data.itemId === state.sync.itemId) {
            set("#sync-bar", (el) => { el.style.width = `${data.percent}%`; });
            set("#sync-step", (el) => { el.textContent = data.step; });
          }
        }
        break;
      }
      case "reencode-changed": {
        const finished = state.reencode.running && !data.running;
        applyReencodeState(data);
        if (state.view === "reencode") render(); else renderTopbar();
        if (reencodeModal?.kind === "trial" || (reencodeModal && state.reencode.jobs[reencodeModal.itemId]?.state !== "done")) renderReencodeModal();
        if (finished && reDone().length) toast("Re-encoding finished — compare, then keep or undo in the Re-encode tab", "ok", 8000);
        break;
      }
      case "reencode-progress": {
        const job = state.reencode.jobs[data.itemId];
        if (job) Object.assign(job, data);
        renderTopbar();
        renderReencodeRow(data.itemId);
        break;
      }
      case "reencode-trial":
        if (state.reencode.trial) Object.assign(state.reencode.trial, data);
        if (reencodeModal?.kind === "trial") renderReencodeModal();
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
        if (data.state === "downloaded" && state.view === "settings") render();
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
      // Subtitle files dropped on the Sync tab join the comparison of the video it shows.
      const subtitles = state.view === "sync" ? paths.filter(isSubtitlePath) : [];
      if (subtitles.length && !state.sync.itemId) {
        toast("Open a video in the list first, then drop its subtitles", "err");
        return;
      }
      if (subtitles.length) {
        const { added } = await post("/api/sync/add", { itemId: state.sync.itemId, paths: subtitles });
        toast(added > 0 ? `Added ${added} subtitle${added > 1 ? "s" : ""} to compare` : "Already in the comparison", added > 0 ? "ok" : "info");
      }
      const others = paths.filter((p) => !subtitles.includes(p));
      if (!others.length) return;
      const result = await post("/api/library/add", { paths: others });
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
  const encoding = reencodeProgressText();
  const listening = syncProgressText();
  progress.hidden = !state.searching && !encoding && !listening;
  document.getElementById("top-progress-text").textContent =
    [state.searching ? `Searching ${state.progress.done}/${state.progress.total}` : "", encoding, listening].filter(Boolean).join(" · ");
}

function renderMain() {
  const main = document.getElementById("main");
  // The Re-encode view listens on the container itself; no other view may inherit that.
  main.onclick = null;
  main.onchange = null;
  if (state.view === "files") renderFilesView(main);
  else if (state.view === "reencode") renderReencodeView(main);
  else if (state.view === "sync") renderSyncView(main);
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
        <tbody>${mediaGroups().map((group) => fileGroupRowHtml(group) + group.items.map(renderFileRowHtml).join("")).join("")}</tbody>
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

function fileGroupRowHtml(group) {
  const count = `${group.items.length} file${group.items.length === 1 ? "" : "s"}`;
  let name = esc(group.name), detail = count;
  if (group.kind === "show") detail += ` · ${esc(seasonsLabel(group.items))}`;
  else if (group.kind === "title") { name = `“${name}”`; detail += " · show not known yet"; }
  else if (group.kind === "movieSeries") detail = `${group.items.length} movies`;
  return `<tr class="group-row"><td colspan="5"><b>${name}</b> <span class="hint-text">· ${detail}</span></td></tr>`;
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

  const detectTitle = item.parsed?.isEpisode ? "Click to change the show, season or episode" : "Click to say which show and episode this is";
  return `
    <tr data-row="${item.id}">
      <td class="col-langs">${chips}</td>
      <td title="${esc(item.path)}">${esc(item.fileName)}</td>
      <td class="col-parsed det-cell" data-detect="${item.id}" title="${detectTitle}">
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
  if (!p.isEpisode) {
    return item.unidentified
      ? `<span class="det-warn">not recognised — click to pick the show</span>`
      : `${esc(p.title || "")} ${tag ? `<span class="ep">${esc(tag)}</span>` : ""} ${nameSourceBadge(item)}`;
  }

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

  return `${name} <span class="ep">${esc(tag)}</span> ${badge} ${nameSourceBadge(item)} ${extra}`;
}

// How a file was identified when its filename was not what told us.
const NAME_SOURCES = {
  decodedFileName: ["decoded name", "The filename is Base64 — it was decoded to"],
  embeddedTitle: ["embedded title", "The filename says nothing usable — the title stored inside the video file was used"],
  fileHash: ["file hash", "The filename says nothing usable — the file was recognised by its hash (size + checksum) on OpenSubtitles"],
  user: ["set by you", "You said which show and episode this file is"],
};

function nameSourceText(item) {
  const entry = NAME_SOURCES[item.nameSource];
  if (!entry) return "";
  return entry[1] + (item.sourceName ? `: ${item.sourceName}` : "");
}

function nameSourceBadge(item) {
  const entry = NAME_SOURCES[item.nameSource];
  return entry ? `<span class="guess-badge source" title="${esc(nameSourceText(item))}">${entry[0]}</span>` : "";
}

// The blocks the server sorted the files into: one per show (whatever name each file uses for
// it) or movie series, each show split into season blocks (same folder, same name pattern).
function mediaGroups(items = state.items) {
  const groups = new Map();
  for (const item of items) {
    const g = item.group || { key: "other", kind: "other", name: "Other files", block: null };
    if (!groups.has(g.key)) groups.set(g.key, { key: g.key, kind: g.kind, name: g.name, items: [], blocks: new Map() });
    const group = groups.get(g.key);
    group.items.push(item);
    if (g.block != null) {
      if (!group.blocks.has(g.block)) group.blocks.set(g.block, { key: g.block, items: [] });
      group.blocks.get(g.block).items.push(item);
    }
  }
  return [...groups.values()].map((g) => ({ ...g, blocks: [...g.blocks.values()] }));
}

// The group a file is in right now (its key changes when the user picks another show).
function groupOfItem(itemId) {
  const item = state.items.find((i) => i.id === itemId);
  return item ? mediaGroups().find((g) => g.key === (item.group?.key ?? "other")) : null;
}

// "Season 2", "Seasons 1, 2", "Specials" — the seasons a set of files is seen as.
function seasonsLabel(items) {
  const seasons = [...new Set(items.map((i) => i.effective?.season).filter((s) => s != null))].sort((a, b) => a - b);
  if (!seasons.length) return "Season not known";
  const real = seasons.filter((s) => s > 0);
  const parts = [];
  if (real.length) parts.push(`Season${real.length > 1 ? "s" : ""} ${real.join(", ")}`);
  if (seasons.includes(0)) parts.push("Specials");
  return parts.join(" + ");
}

// The user's show pick applies to every filename title of the group.
async function chooseShow(itemId, show) {
  const group = groupOfItem(itemId);
  if (!group) return;
  const titles = [...new Set(group.items.filter((i) => i.parsed?.isEpisode).map((i) => i.parsed.title))];
  try {
    for (const title of titles) await post("/api/library/show", { title, show });
    await loadLibrary();
  } catch (err) {
    toast(`Could not set the show: ${err.message}`, "err");
  }
}

async function setSeason(itemIds, season) {
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

// Season picker of one season block. It only matters to files whose name carries no season.
function seasonSelectHtml(block) {
  const current = block.items.find((i) => i.seasonOverride != null)?.seasonOverride ?? null;
  const known = block.items.find((i) => i.detection?.seasons?.length)?.detection.seasons;
  const seasons = known || Array.from({ length: 12 }, (_, i) => i + 1);
  return `
    <select class="input season-select" data-block="${esc(block.key)}" title="Season for these files — their names carry only an episode number">
      <option value="">Season: auto</option>
      ${seasons.map((s) => `<option value="${s}" ${current === s ? "selected" : ""}>Season ${s}</option>`).join("")}
    </select>`;
}

// A block whose files all spell out their season has nothing to pick.
function blockNeedsSeason(block) {
  return block.items.some((i) => i.parsed?.season == null);
}

// Binds the show list + search box + season pickers inside `root` for the group of one file.
function bindShowPicker(root, itemId, onChanged) {
  root.querySelectorAll("[data-show]").forEach((el) => {
    el.onclick = async () => {
      await chooseShow(itemId, JSON.parse(el.dataset.show));
      onChanged?.();
    };
  });
  root.querySelectorAll(".season-select").forEach((select) => {
    select.onchange = async () => {
      const block = groupOfItem(itemId)?.blocks.find((b) => b.key === select.dataset.block);
      if (!block) return;
      await setSeason(block.items.map((i) => i.id), Number(select.value));
      onChanged?.();
    };
  });

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
        el.onclick = async () => { await chooseShow(itemId, JSON.parse(el.dataset.show)); onChanged?.(); };
      });
    }, 400);
  }
}

// One line per season block: what it is seen as, where it is, and its season picker.
function blockLineHtml(group, block) {
  const folders = new Set(group.items.map((i) => i.group?.folder));
  const folder = folders.size > 1 ? block.items[0].group?.folder : "";
  return `<b>${esc(seasonsLabel(block.items))}</b>
    <span class="hint-text">· ${block.items.length} file${block.items.length === 1 ? "" : "s"}${folder ? ` · in “${esc(folder)}”` : ""}</span>
    ${blockNeedsSeason(block) ? seasonSelectHtml(block) : ""}`;
}

let detectionModalToken = 0;

function openDetectionModal(itemId) {
  const item = state.items.find((i) => i.id === itemId);
  if (!item) return;
  if (!item.parsed?.isEpisode) { openIdentifyModal(itemId); return; }
  const token = ++detectionModalToken;
  const root = document.getElementById("modal-root");
  const close = () => { detectionModalToken++; root.innerHTML = ""; };

  const draw = () => {
    if (token !== detectionModalToken) return;
    const group = groupOfItem(itemId);
    if (!group) { close(); return; }
    const files = group.items;
    const d = files[0]?.detection;
    const current = d?.show || null;
    const candidates = d?.candidates || [];
    root.innerHTML = `
      <div class="modal-backdrop">
        <div class="modal">
          <div class="modal-head">
            <span class="title">${group.kind === "show" ? esc(group.name) : `“${esc(group.name)}”`} — ${files.length} file${files.length === 1 ? "" : "s"}</span>
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
              ${group.blocks.map((block) => `<div class="block-line">${blockLineHtml(group, block)}</div>`).join("")}
            </div>
            <div class="detect-section">
              <div class="detect-label">Files</div>
              <div class="hint-text" style="margin-bottom:6px">Wrong show or episode for one file only? Use <i>Identify</i> on its line.</div>
              ${files.map((i) => `<div class="detect-file"><button class="btn small secondary" data-identify="${esc(i.id)}">Identify…</button> <span class="ep">${esc(episodeTag(i))}</span> ${esc(i.fileName)}${i.guess && i.guess.confidence !== "explicit" ? ` <span class="hint-text">— ${esc(i.guess.reason)}</span>` : ""}</div>`).join("")}
            </div>
          </div>
        </div>
      </div>`;
    root.querySelector("#modal-close").onclick = close;
    root.querySelector(".modal-backdrop").onclick = (e) => { if (e.target.classList.contains("modal-backdrop")) close(); };
    root.querySelectorAll("[data-identify]").forEach((btn) => { btn.onclick = () => openIdentifyModal(btn.dataset.identify); });
    bindShowPicker(root, itemId, draw);
  };
  draw();
}

// ---------- Identify one file (show + season + episode, picked by hand) --------------------------

function seasonLabel(season) {
  return season === 0 ? "Specials" : `Season ${season}`;
}

function openIdentifyModal(itemId) {
  const item = state.items.find((i) => i.id === itemId);
  if (!item) return;
  const token = ++detectionModalToken;
  const root = document.getElementById("modal-root");
  const alive = () => token === detectionModalToken;
  const close = () => { detectionModalToken++; root.innerHTML = ""; };

  const d = item.detection;
  const picked = {
    show: d?.show || null,
    episodes: null, // null = not loaded, [] = the sources list none
    season: item.effective?.season ?? null,
    episode: item.effective?.episode ?? null,
    saving: false,
  };
  const suggestions = d && d.status !== "resolved" ? d.candidates || [] : [];
  const source = nameSourceText(item);

  root.innerHTML = `
    <div class="modal-backdrop">
      <div class="modal">
        <div class="modal-head">
          <span class="title" title="${esc(item.fileName)}">Identify “${esc(item.fileName)}”</span>
          <button class="icon-btn" id="modal-close" title="Close">&#10005;</button>
        </div>
        <div class="modal-body detect-body">
          ${source ? `<div class="hint-text identify-source">${esc(source)}</div>` : ""}
          <div class="detect-section">
            <div class="detect-label">Show</div>
            <div class="identify-show"></div>
            <input class="input show-search" placeholder="Type the show's name…" style="margin-top:8px" autocomplete="off">
            <div class="show-search-results">${suggestions.map((s) => showResultHtml(s, false)).join("")}</div>
          </div>
          <div class="detect-section">
            <div class="detect-label">Episode</div>
            <div class="identify-episode"></div>
          </div>
          <div class="identify-actions">
            ${item.nameSource === "user" ? `<button class="btn secondary" id="identify-reset" title="Forget your choice and detect this file automatically again">Back to automatic</button>` : ""}
            <div class="grow"></div>
            <button class="btn secondary" id="identify-cancel">Cancel</button>
            <button class="btn" id="identify-apply" disabled>Use this episode</button>
          </div>
        </div>
      </div>
    </div>`;

  const showBox = root.querySelector(".identify-show");
  const episodeBox = root.querySelector(".identify-episode");
  const results = root.querySelector(".show-search-results");
  const input = root.querySelector(".show-search");
  const applyBtn = root.querySelector("#identify-apply");

  const refreshApply = () => {
    applyBtn.disabled = picked.saving || !picked.show || picked.season == null || !(picked.episode >= 1);
    applyBtn.textContent = picked.saving ? "Saving…" : "Use this episode";
  };

  const drawShow = () => {
    showBox.innerHTML = picked.show
      ? showResultHtml(picked.show, true)
      : `<div class="hint-text">Type a few letters of the show's name and pick it from the suggestions.</div>`;
  };

  const drawEpisode = () => {
    if (!picked.show) {
      episodeBox.innerHTML = `<div class="hint-text">Pick the show first.</div>`;
    } else if (picked.episodes === null) {
      episodeBox.innerHTML = `<div class="hint-text"><span class="spin"></span> Loading the episode list…</div>`;
    } else if (!picked.episodes.length) {
      // No episode list to choose from: plain numbers still let the search run.
      episodeBox.innerHTML = `
        <div class="hint-text" style="margin-bottom:6px">No episode list for this show — enter the numbers (season 0 = specials).</div>
        <div class="identify-row">
          <label>Season <input class="input" id="identify-season" type="number" min="0" value="${picked.season ?? 1}"></label>
          <label>Episode <input class="input" id="identify-number" type="number" min="1" value="${picked.episode ?? ""}"></label>
        </div>`;
      const seasonInput = episodeBox.querySelector("#identify-season");
      const numberInput = episodeBox.querySelector("#identify-number");
      const read = () => {
        picked.season = seasonInput.value === "" ? null : Number(seasonInput.value);
        picked.episode = numberInput.value === "" ? null : Number(numberInput.value);
        refreshApply();
      };
      seasonInput.oninput = read;
      numberInput.oninput = read;
      read();
    } else {
      // Specials (OVAs…) are always on offer, even when no source lists them by number.
      const seasons = [...new Set([...picked.episodes.map((e) => e.season), 0])].sort((a, b) => (a === 0) - (b === 0) || a - b);
      if (!seasons.includes(picked.season)) { picked.season = seasons[0]; picked.episode = null; }
      const inSeason = picked.episodes.filter((e) => e.season === picked.season);
      const listed = inSeason.length > 0;
      if (listed && !inSeason.some((e) => e.number === picked.episode)) picked.episode = null;
      episodeBox.innerHTML = `
        <div class="identify-row">
          <select class="input" id="identify-season">
            ${seasons.map((s) => `<option value="${s}" ${s === picked.season ? "selected" : ""}>${seasonLabel(s)}</option>`).join("")}
          </select>
          ${listed ? `
            <select class="input grow" id="identify-number">
              <option value="">Which episode?</option>
              ${inSeason.map((e) => `<option value="${e.number}" ${e.number === picked.episode ? "selected" : ""}>E${String(e.number).padStart(2, "0")}${e.name ? " — " + esc(e.name) : ""}</option>`).join("")}
            </select>` : `
            <label>Number <input class="input" id="identify-number" type="number" min="1" value="${picked.episode ?? ""}"></label>`}
        </div>
        ${listed ? "" : `<div class="hint-text" style="margin-top:6px">The specials of this show are not listed by number — enter the special's number. Add a TMDB key in Settings to pick them by title.</div>`}`;
      episodeBox.querySelector("#identify-season").onchange = (e) => {
        picked.season = Number(e.target.value);
        picked.episode = null;
        drawEpisode();
      };
      const numberField = episodeBox.querySelector("#identify-number");
      numberField.onchange = numberField.oninput = () => {
        picked.episode = numberField.value === "" ? null : Number(numberField.value);
        refreshApply();
      };
    }
    refreshApply();
  };

  const pickShow = async (show) => {
    picked.show = show;
    picked.episodes = null;
    input.value = "";
    results.innerHTML = "";
    drawShow();
    drawEpisode();
    let episodes = [];
    try { episodes = await post("/api/shows/episodes", show); } catch { /* numbers by hand then */ }
    if (!alive() || !isSameShow(picked.show, show)) return;
    picked.episodes = episodes;
    drawEpisode();
  };

  const bindResults = () => {
    results.querySelectorAll("[data-show]").forEach((el) => { el.onclick = () => pickShow(JSON.parse(el.dataset.show)); });
  };

  let searchToken = 0;
  input.oninput = debounce(async () => {
    const q = input.value.trim();
    const mine = ++searchToken;
    if (q.length < 2) { results.innerHTML = ""; return; }
    let found = [];
    try { found = await get(`/api/rename/shows?q=${encodeURIComponent(q)}`); } catch { /* ignore */ }
    if (!alive() || mine !== searchToken) return;
    results.innerHTML = found.length
      ? found.slice(0, 8).map((s) => showResultHtml(s, false)).join("")
      : `<div class="hint-text" style="padding:6px 2px">No show found for “${esc(q)}”.</div>`;
    bindResults();
  }, 300);

  const save = async (body, done) => {
    picked.saving = true;
    refreshApply();
    try {
      await post("/api/library/identify", body);
      await loadLibrary();
      if (state.view === "rename") loadProposals();
      close();
      toast(done, "ok");
    } catch (err) {
      picked.saving = false;
      refreshApply();
      toast(`Could not identify the file: ${err.message}`, "err");
    }
  };

  applyBtn.onclick = () => save(
    { itemId, show: picked.show, season: picked.season, episode: picked.episode },
    `${picked.show.name} ${picked.season === 0 ? "special" : "S" + String(picked.season).padStart(2, "0")} E${String(picked.episode).padStart(2, "0")} — set`);
  const resetBtn = root.querySelector("#identify-reset");
  if (resetBtn) resetBtn.onclick = () => save({ itemId, show: null }, "Back to automatic detection");
  root.querySelector("#identify-cancel").onclick = close;
  root.querySelector("#modal-close").onclick = close;
  root.querySelector(".modal-backdrop").onclick = (e) => { if (e.target.classList.contains("modal-backdrop")) close(); };

  bindResults();
  drawShow();
  if (picked.show) pickShow(picked.show); else drawEpisode();
  input.focus();
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
    ${mediaGroups().map((group) => renderRenameGroupHtml(group, byItem)).join("")}`;

  main.querySelectorAll(".rename-group").forEach((card) => {
    if (card.dataset.item) bindShowPicker(card, card.dataset.item, () => loadProposals());
    card.querySelectorAll("[data-change]").forEach((btn) => { btn.onclick = () => openDetectionModal(btn.dataset.change); });
    card.querySelectorAll("[data-identify]").forEach((btn) => { btn.onclick = () => openIdentifyModal(btn.dataset.identify); });
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
  const episodes = group.kind === "show" || group.kind === "title";
  const titles = [...new Set(group.items.map((i) => i.parsed?.title || ""))];
  const quoted = titles.map((t) => `“${t}”`).join(", ");
  // One season block: its picker sits in the header. Several: each gets its own line below.
  const single = group.blocks.length === 1 ? group.blocks[0] : null;

  let head;
  if (group.kind === "movieSeries") {
    head = `<div class="panel-title">${esc(group.name)} — ${group.items.length} movies</div>
      <div class="hint-text" style="padding:0 14px 10px">Movies are left alone. If one of them is an episode, <i>Identify</i> it.</div>`;
  } else if (!episodes) {
    head = `<div class="panel-title">Other files</div>
      <div class="hint-text" style="padding:0 14px 10px">Movies and files without an episode number are left alone. If one of them is an episode, <i>Identify</i> it.</div>`;
  } else if (!d) {
    head = `<div class="panel-title">“${esc(group.name)}”</div>
      <div class="hint-text" style="padding:0 14px 10px"><span class="spin"></span> Detecting the show…</div>`;
  } else if (d.status === "resolved") {
    const s = d.show;
    head = `
      <div class="group-head">
        ${s.imageUrl ? `<img src="${esc(s.imageUrl)}" alt="">` : `<div class="img-placeholder"></div>`}
        <div class="meta">
          <div class="name">${esc(s.name)}</div>
          <div class="sub">${esc([s.premiered?.slice(0, 4), s.network, s.source, d.chosenByUser ? "chosen by you" : `detected from ${quoted}`].filter(Boolean).join(" · "))}</div>
        </div>
        ${single && blockNeedsSeason(single) ? seasonSelectHtml(single) : ""}
        <button class="btn small secondary" data-change="${esc(first.id)}">Change show…</button>
      </div>`;
  } else {
    const why = d.status === "ambiguous"
      ? `Several shows match ${esc(quoted)} — which one is it?`
      : `No show found for ${esc(quoted)} — pick one or search for it:`;
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

  const rowHtml = (item) => {
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
          ${episodes ? "" : `<button class="btn small secondary" data-identify="${esc(item.id)}">Identify…</button>`}
          ${p.note ? `<span class="guess-badge ${item.guess?.confidence === "low" ? "low" : ""}" title="${esc(p.note)}">guessed</span>` : ""}
        </td>
      </tr>`;
  };
  const rows = episodes && !single && d?.status === "resolved"
    ? group.blocks.map((block) => `
        <tr class="block-row"><td colspan="4"><div class="block-line">${blockLineHtml(group, block)}</div></td></tr>
        ${block.items.map(rowHtml).join("")}`).join("")
    : group.items.map(rowHtml).join("");

  return `
    <div class="card rename-group" data-item="${episodes ? esc(first.id) : ""}">
      ${head}
      <table class="rename-table">
        <thead><tr><th class="col-check"></th><th>Current name</th><th class="arrow"></th><th>New name</th></tr></thead>
        <tbody>${rows}</tbody>
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
        <h3>Re-encoding</h3>
        <div class="desc">The Re-encode tab works with ffmpeg: the one Subtitall downloaded, else the one on your PATH. Give a folder here only to use another one (it must contain both <code>ffmpeg</code> and <code>ffprobe</code>).</div>
        <div class="settings-row"><label>ffmpeg folder</label><input class="input" id="ffmpeg-folder" value="${esc(reSettings().ffmpegPath)}" placeholder="Automatic" style="max-width:430px"></div>
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
      reencode: { ...reSettings(), ffmpegPath: main.querySelector("#ffmpeg-folder").value.trim() },
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
      state.reencode.ffmpeg = null; // the ffmpeg folder may have changed: ask again
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
  if (install) install.onclick = installUpdate;

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

// Asks the app to install the downloaded update. One click only: the whole window is covered at
// once, so nothing can be clicked while the app closes and the installer takes over.
let installRequested = false;

async function installUpdate() {
  if (installRequested) return;
  installRequested = true;
  renderInstallOverlay();
  try {
    await post("/api/update/install");
  } catch (err) {
    installRequested = false;
    renderInstallOverlay();
    toast(`Could not start the update: ${err.message}`, "err");
  }
}

function renderInstallOverlay() {
  const installing = installRequested || state.update?.state === "installing";
  let overlay = document.getElementById("install-overlay");
  if (!installing) { overlay?.remove(); return; }
  if (overlay) return;
  overlay = document.createElement("div");
  overlay.id = "install-overlay";
  overlay.className = "install-overlay";
  overlay.innerHTML = `<div><span class="spin"></span> Installing the update…</div>
    <div class="hint-text">Subtitall closes and reopens by itself. This takes a few seconds.</div>`;
  document.body.appendChild(overlay);
}

function renderUpdateStatus() {
  const u = state.update || {};
  // A failed install gives the window back (the backend reports an error state).
  if (installRequested && u.state === "error") installRequested = false;
  renderInstallOverlay();
  const top = document.getElementById("top-update");
  if (top) {
    const busy = u.state === "checking" || u.state === "downloading" || u.state === "available";
    top.hidden = !(busy || u.state === "downloaded");
    if (u.state === "downloaded") {
      top.innerHTML = `<button class="btn small" id="top-install-update" title="${esc(u.message || "")}">Restart to update to v${esc(u.version || "")}</button>`;
      top.querySelector("#top-install-update").onclick = installUpdate;
    } else if (busy) {
      const pct = u.state === "downloading" && u.percent != null ? ` ${Math.round(u.percent)}%` : "";
      top.innerHTML = `<span class="spin"></span> ${esc(u.state === "checking" ? "Checking for updates…" : `Downloading update${u.version ? ` v${u.version}` : ""}…${pct}`)}`;
    } else {
      top.innerHTML = "";
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

// ---------- Re-encode view ---------------------------------------------------------------------------

const RE_LEVELS = {
  high: ["Best quality", "Closest to the original, smallest gain"],
  balanced: ["Balanced", "Recommended — no visible difference on most videos"],
  compact: ["Smallest", "Biggest gain — a trained eye may notice"],
};
const RE_CODECS = {
  hevc: ["H.265", "libx265", "recommended"],
  av1: ["AV1", "libsvtav1", "smaller, slower, recent devices only"],
  h264: ["H.264", "libx264", "bigger files, plays everywhere"],
};
const RE_SPEEDS = { fast: "Fast", normal: "Normal", slow: "Slow (smaller files)" };
const RE_HEIGHTS = { 0: "Keep the resolution", 2160: "At most 4K (2160p)", 1080: "At most 1080p", 720: "At most 720p", 480: "At most 480p" };

function reSettings() {
  return { ffmpegPath: "", codec: "hevc", quality: "balanced", speed: "normal", maxHeight: 0, ...(state.settings?.reencode || {}) };
}

function applyReencodeState(s) {
  const r = state.reencode;
  r.running = s.running;
  r.jobs = Object.fromEntries(s.jobs.map((j) => [j.itemId, j]));
  r.trial = s.trial;
}

async function loadReencodeStatus() {
  const status = await get("/api/reencode/status");
  state.reencode.ffmpeg = status.ffmpeg;
  applyReencodeState(status.state);
}

// Guards against an older analysis landing after a newer one.
let reFilesToken = 0;

async function loadReencodeFiles() {
  const token = ++reFilesToken;
  const r = state.reencode;
  try {
    const list = await post("/api/reencode/analyze");
    if (token !== reFilesToken) return;
    r.files = Object.fromEntries(list.map((f) => [f.itemId, f]));
    // Ticked by default: the files worth re-encoding. The user's own ticks are kept.
    for (const f of list) if (!(f.itemId in r.checks)) r.checks[f.itemId] = f.advice.recommended;
  } catch (err) {
    if (token !== reFilesToken) return;
    r.files = {};
    toast(`Could not read the videos: ${err.message}`, "err");
  }
  if (state.view === "reencode") render();
}

const loadReencodeFilesDebounced = debounce(() => loadReencodeFiles().catch(console.error), 400);

async function saveReencodeSettings(changes) {
  try {
    state.settings = await put("/api/settings", { ...state.settings, reencode: { ...reSettings(), ...changes } });
  } catch (err) {
    toast(`Could not save: ${err.message}`, "err");
  }
}

function formatDuration(seconds) {
  if (seconds == null || !isFinite(seconds)) return "";
  if (seconds < 60) return "less than a minute left";
  const minutes = Math.round(seconds / 60);
  if (minutes < 60) return `${minutes} min left`;
  return `${Math.floor(minutes / 60)} h ${String(minutes % 60).padStart(2, "0")} left`;
}

function formatClock(seconds) {
  const s = Math.max(0, Math.round(seconds));
  const h = Math.floor(s / 3600), m = Math.floor((s % 3600) / 60);
  return `${h ? h + ":" + String(m).padStart(2, "0") : m}:${String(s % 60).padStart(2, "0")}`;
}

function gainHtml(percent) {
  if (percent == null) return "";
  return percent > 0
    ? `<b class="re-gain">−${percent}%</b>`
    : `<b class="re-gain bad">+${Math.abs(percent)}%</b>`;
}

function videoInfoText(info) {
  if (!info) return "";
  const parts = [info.codecName, `${info.width}×${info.height}`];
  if (info.bitRate) parts.push(`${(info.bitRate / 1e6).toFixed(info.bitRate >= 1e7 ? 0 : 1)} Mb/s`);
  if (info.hdr) parts.push("HDR");
  return parts.join(" · ");
}

// ---- ffmpeg: guide and install --------------------------------------------------------------------

function codeHtml(command) {
  return `<span class="cmd"><code>${esc(command)}</code><button class="btn small secondary" data-copy="${esc(command)}">Copy</button></span>`;
}

function ffmpegDownloadHtml(f) {
  const busy = f.state === "downloading" || f.state === "installing";
  if (busy) {
    const amount = f.totalBytes ? `${formatSize(f.downloadedBytes)} of ${formatSize(f.totalBytes)}` : "";
    return `
      <div class="re-progress big"><div style="width:${f.state === "installing" ? 100 : f.percent || 0}%"></div></div>
      <div class="re-install-line">
        <span><span class="spin"></span> ${esc(f.message || "Working…")} ${amount && f.state === "downloading" ? `<span class="hint-text">${amount}</span>` : ""}</span>
        <button class="btn small secondary" data-re="ffmpeg-cancel">Cancel</button>
      </div>`;
  }
  if (f.state !== "error")
    return `<button class="btn" data-re="ffmpeg-install">Download and install ffmpeg</button>`;
  return `
    <div class="re-failure">
      <div class="warn-text">${esc(f.message || "ffmpeg could not be installed.")}</div>
      ${f.tried?.length ? `<ul>${f.tried.map((t) => `<li>${esc(t)}</li>`).join("")}</ul>` : ""}
      ${f.blocked ? `
        <p><b>What you can do:</b></p>
        <ul>
          <li>If ${f.os === "windows" ? "Windows Security" : "your system"} showed a notification about a blocked program with an <i>Allow</i> or <i>Unblock</i> button, click it, then click <i>Check again</i>.</li>
          <li>Wait and click <i>Check again</i> later: some protections only distrust programs they have never seen, and let them run after a while.</li>
          <li>Ask your administrator to allow ffmpeg, or install it yourself (below).</li>
        </ul>` : ""}
    </div>
    <div class="re-own-row">
      <button class="btn" data-re="ffmpeg-install">Try again</button>
      ${f.hasDownload ? `<button class="btn secondary" data-re="ffmpeg-recheck">Check again</button>
        <button class="btn secondary" data-re="ffmpeg-remove" title="Deletes ffmpeg.exe and ffprobe.exe from Subtitall's folder">Delete the blocked download</button>` : ""}
    </div>`;
}

function ffmpegOwnHtml(f) {
  const path = `
    <div class="re-own-row">
      <input class="input" id="re-ffmpeg-path" value="${esc(reSettings().ffmpegPath)}" placeholder="${f.os === "windows" ? "C:\\Tools\\ffmpeg\\bin" : "/opt/ffmpeg/bin"}">
      <button class="btn secondary" data-re="ffmpeg-recheck">Check again</button>
    </div>`;
  if (f.os === "windows") {
    return `
      <p>Install it with Windows' own package manager — in a terminal (PowerShell):</p>
      ${codeHtml("winget install Gyan.FFmpeg")}
      <p>then click <i>Check again</i>. Or, if you already have ffmpeg somewhere, give the folder that contains <code>ffmpeg.exe</code> and <code>ffprobe.exe</code>:</p>
      ${path}`;
  }
  if (f.os === "mac") {
    return `
      <p>Install it with <a href="https://brew.sh" target="_blank">Homebrew</a>, in a terminal:</p>
      ${codeHtml("brew install ffmpeg")}
      <p>then click <i>Check again</i>. Subtitall looks for <code>ffmpeg</code> and <code>ffprobe</code> on your PATH. An app started from the Dock does not always see Homebrew's folder — if it is not found, give it here (<code>/opt/homebrew/bin</code> on Apple Silicon, <code>/usr/local/bin</code> on Intel):</p>
      ${path}`;
  }
  return `
    <p>Subtitall uses the <code>ffmpeg</code> and <code>ffprobe</code> commands found on your <code>PATH</code>. Install them with your package manager:</p>
    <table class="re-distros">
      <tr><td>Debian, Ubuntu, Mint</td><td>${codeHtml("sudo apt install ffmpeg")}</td></tr>
      <tr><td>Fedora</td><td>${codeHtml("sudo dnf install ffmpeg --allowerasing")}<div class="hint-text">Needs the <a href="https://rpmfusion.org/Configuration" target="_blank">RPM Fusion</a> repository — Fedora's own <code>ffmpeg-free</code> cannot encode H.265.</div></td></tr>
      <tr><td>Arch, Manjaro</td><td>${codeHtml("sudo pacman -S ffmpeg")}</td></tr>
      <tr><td>openSUSE</td><td>${codeHtml("sudo zypper install ffmpeg")}<div class="hint-text">With the Packman repository for H.265.</div></td></tr>
    </table>
    <p>Check it in a terminal — both commands must answer, and the second must list <code>libx265</code>:</p>
    ${codeHtml("ffprobe -version && ffmpeg -hide_banner -encoders | grep 265")}
    <p>then click <i>Check again</i>. If Subtitall was started from a desktop launcher, its PATH can differ from your terminal's (<code>~/.local/bin</code>, <code>~/bin</code>…). In that case, or for a build of your own, give the folder that contains both programs:</p>
    ${path}`;
}

function renderFfmpegGuide(main, purpose = "Re-encoding") {
  const f = state.reencode.ffmpeg;
  const busy = f.state === "downloading" || f.state === "installing";
  const folder = esc(f.installFolder);
  const what = f.os === "mac"
    ? `the ffmpeg build for macOS from <b>evermeet.cx</b> (linked by ffmpeg.org; an Intel build — on Apple Silicon it runs through Rosetta)`
    : `the official ffmpeg build for ${f.os === "windows" ? "Windows" : "Linux"} (about 200 MB) from <b>github.com/BtbN/FFmpeg-Builds</b>, the builds ffmpeg.org links to`;
  const auto = `
    <ol class="re-steps">
      <li>Subtitall downloads ${what}.</li>
      <li>It checks that the download is intact${f.os === "mac" ? "" : " (SHA-256 checksum)"}.</li>
      <li>It unpacks <code>ffmpeg</code> and <code>ffprobe</code> into its own folder, <code>${folder}</code>. Nothing else on your computer is changed and no administrator rights are needed.</li>
      ${f.fallbacks?.length ? `<li>Only if that download cannot be used — some company computers refuse to start a freshly downloaded program — Subtitall installs ffmpeg with what your computer already has: ${f.fallbacks.map((x) => `<b>${esc(x)}</b>`).join(", then ")}.</li>` : ""}
    </ol>
    ${f.canDownload ? ffmpegDownloadHtml(f) : `<div class="warn-text">No ready-made ffmpeg exists for this kind of computer — install it yourself.</div>`}`;

  const intro = `
    <div class="re-guide-head">
      <h2>One thing to set up first</h2>
      <p>${purpose} is done by <b>ffmpeg</b>, a free and open-source video tool that Subtitall does not ship with. It is installed once, then this tab just works.</p>
    </div>`;

  if (f.os === "windows") {
    main.innerHTML = `
      <div class="card re-guide">
        ${intro}
        <div class="re-choice">
          <h3>Let Subtitall install it <span class="badge-rec">recommended</span></h3>
          ${auto}
        </div>
        <details class="re-choice" ${state.reencode.showOwn || f.state === "error" ? "open" : ""} id="re-own">
          <summary>I already have ffmpeg, or I'd rather install it myself</summary>
          ${ffmpegOwnHtml(f)}
        </details>
      </div>`;
  } else {
    // Linux and macOS users often want their system's ffmpeg: both ways get the same weight.
    main.innerHTML = `
      <div class="card re-guide">
        ${intro}
        <div class="re-choices">
          <div class="re-choice">
            <h3>Let Subtitall download it</h3>
            <p class="hint-text">Fully automatic, nothing to type. Your system is left untouched.</p>
            ${auto}
          </div>
          <div class="re-choice">
            <h3>Use my system's ffmpeg</h3>
            <p class="hint-text">Kept up to date by your package manager.</p>
            ${ffmpegOwnHtml(f)}
          </div>
        </div>
      </div>`;
  }

  const own = main.querySelector("#re-own");
  if (own) own.ontoggle = () => { state.reencode.showOwn = own.open; };
  main.querySelectorAll("[data-copy]").forEach((btn) => {
    btn.onclick = async (e) => {
      e.stopPropagation();
      try { await navigator.clipboard.writeText(btn.dataset.copy); btn.textContent = "Copied ✓"; }
      catch { toast("Could not copy — select the command and copy it by hand", "err"); }
    };
  });
  main.onclick = async (e) => {
    const action = e.target.closest("[data-re]")?.dataset.re;
    if (!action) return;
    try {
      if (action === "ffmpeg-install") await post("/api/reencode/ffmpeg/install");
      else if (action === "ffmpeg-cancel") await post("/api/reencode/ffmpeg/cancel");
      else if (action === "ffmpeg-remove") { state.reencode.ffmpeg = await post("/api/reencode/ffmpeg/remove"); render(); }
      else if (action === "ffmpeg-recheck") {
        const input = main.querySelector("#re-ffmpeg-path");
        if (input && input.value.trim() !== reSettings().ffmpegPath) await saveReencodeSettings({ ffmpegPath: input.value.trim() });
        state.reencode.ffmpeg = await post("/api/reencode/ffmpeg/recheck");
        if (state.reencode.ffmpeg.state === "ready") toast(`ffmpeg ${state.reencode.ffmpeg.version} found`, "ok");
        else toast(state.reencode.ffmpeg.message || "ffmpeg and ffprobe were not found", "err", 7000);
        render();
      }
    } catch (err) {
      toast(err.message, "err");
    }
  };
  if (busy) main.querySelector("#re-ffmpeg-path")?.setAttribute("disabled", "");
}

// ---- File list --------------------------------------------------------------------------------------

// The files of a group a click on "Re-encode" would start: ticked, encodable, not already handled.
function reSelected(group) {
  const r = state.reencode;
  return group.items.filter((i) => r.checks[i.id] && r.files?.[i.id]?.advice.possible && (!r.jobs[i.id] || r.jobs[i.id].state === "failed"));
}

function reDone(items = state.items) {
  return items.map((i) => state.reencode.jobs[i.id]).filter((j) => j?.state === "done");
}

function reStatusHtml(item) {
  const r = state.reencode;
  const job = r.jobs[item.id];
  const file = r.files?.[item.id];
  if (!job) {
    if (!file) return `<span class="hint-text">…</span>`;
    return file.advice.recommended ? "" : `<span class="hint-text" title="You can still tick it">${esc(file.advice.reason)}</span>`;
  }
  switch (job.state) {
    case "queued":
      return `<span class="hint-text">Waiting…</span> <button class="icon-btn" data-re="cancel" data-id="${item.id}" title="Remove from the queue">&#10005;</button>`;
    case "encoding":
      return `
        <div class="re-progress"><div style="width:${job.percent}%"></div></div>
        <span class="re-progress-text">${Math.floor(job.percent)}%${job.etaSeconds != null ? ` · ${formatDuration(job.etaSeconds)}` : ""}</span>
        <button class="icon-btn" data-re="cancel" data-id="${item.id}" title="Stop — the original stays as it is">&#10005;</button>`;
    case "checking":
      return `<span class="hint-text"><span class="spin"></span> Checking the quality…</span>`;
    case "failed":
      return `<span class="warn-text" title="${esc(job.error)}">Failed: ${esc(job.error)}</span> <button class="btn small secondary" data-re="revert" data-id="${item.id}">Dismiss</button>`;
    default: {
      const worse = job.gainPercent <= 0;
      return `
        <span class="re-result" title="Original → re-encoded">${formatSize(job.originalBytes)} → <b>${formatSize(job.newBytes)}</b> ${gainHtml(job.gainPercent)}</span>
        ${worse ? `<span class="warn-text">no gain — undo it</span>`
          : job.quality ? `<span class="re-quality" title="Measured on a few excerpts (SSIM ${job.ssim})">${esc(job.quality)}</span>` : ""}
        <span class="re-row-actions">
          <button class="btn small secondary" data-re="compare" data-id="${item.id}">Compare</button>
          ${worse ? "" : `<button class="btn small" data-re="apply" data-id="${item.id}" title="Delete the original and keep the new file">Keep new</button>`}
          <button class="btn small secondary" data-re="revert" data-id="${item.id}" title="Delete the new file and keep the original">Undo</button>
        </span>`;
    }
  }
}

function reRowHtml(item) {
  const r = state.reencode;
  const file = r.files?.[item.id];
  const job = r.jobs[item.id];
  const locked = !!job && job.state !== "failed";
  return `
    <tr data-re-row="${item.id}">
      <td class="col-check"><input type="checkbox" data-re-check="${item.id}" ${r.checks[item.id] && !locked ? "checked" : ""} ${locked || !file?.advice.possible ? "disabled" : ""}></td>
      <td title="${esc(item.path)}">${esc(item.fileName)}</td>
      <td class="col-video">${esc(videoInfoText(file?.info))}</td>
      <td class="col-size">${formatSize(job?.originalBytes || item.sizeBytes)}</td>
      <td class="col-status" data-re-status="${item.id}">${reStatusHtml(item)}</td>
    </tr>`;
}

function reGroupActionsHtml(group) {
  const selected = reSelected(group);
  const done = reDone(group.items);
  const keepable = done.filter((j) => j.gainPercent > 0);
  const parts = [];
  if (selected.length) {
    parts.push(`<button class="btn small secondary" data-re="trial" data-group="${esc(group.key)}" title="Encode short excerpts of one file at each quality level, and compare">Test on one file</button>`);
    parts.push(`<button class="btn small" data-re="start" data-group="${esc(group.key)}">Re-encode ${selected.length} file${selected.length === 1 ? "" : "s"}</button>`);
  }
  if (done.length > 1) {
    if (keepable.length) parts.push(`<button class="btn small" data-re="apply-group" data-group="${esc(group.key)}" title="Delete the originals and keep the new files">Keep ${keepable.length} new files</button>`);
    parts.push(`<button class="btn small secondary" data-re="revert-group" data-group="${esc(group.key)}" title="Delete the new files and keep the originals">Undo all</button>`);
  }
  return parts.join("");
}

function reGroupSummary(group) {
  const done = reDone(group.items);
  const total = group.items.reduce((sum, i) => sum + (i.sizeBytes || 0), 0);
  let text = `${group.items.length} file${group.items.length === 1 ? "" : "s"} · ${formatSize(total)}`;
  if (done.length) {
    const before = done.reduce((s, j) => s + j.originalBytes, 0), after = done.reduce((s, j) => s + j.newBytes, 0);
    if (before > after) text += ` · ${done.length} re-encoded: ${formatSize(before - after)} to save`;
  }
  return text;
}

function reGroupHtml(group) {
  const name = group.kind === "title" ? `“${esc(group.name)}”` : esc(group.name);
  return `
    <div class="card re-group" data-re-group="${esc(group.key)}">
      <div class="group-head">
        <div class="meta">
          <div class="name">${name}</div>
          <div class="sub" data-re-summary>${esc(reGroupSummary(group))}</div>
        </div>
        <span class="re-group-actions">${reGroupActionsHtml(group)}</span>
      </div>
      <table class="file-table re-table">
        <thead><tr><th class="col-check"></th><th>File</th><th class="col-video">Video</th><th class="col-size">Size</th><th class="col-status">Re-encoding</th></tr></thead>
        <tbody>${group.items.map(reRowHtml).join("")}</tbody>
      </table>
    </div>`;
}

function reOptionsHtml() {
  const s = reSettings();
  const f = state.reencode.ffmpeg;
  const select = (id, current, entries) => `
    <select class="input" id="${id}">
      ${entries.map(([value, label, off]) => `<option value="${value}" ${String(current) === String(value) ? "selected" : ""} ${off ? "disabled" : ""}>${esc(label)}</option>`).join("")}
    </select>`;
  const where = { app: "installed by Subtitall", path: "from your system", custom: "from the folder you set" }[f.source] || "";
  const missing = !f.encoders.includes(RE_CODECS[s.codec][1]);
  return `
    <div class="actionbar re-options">
      <label>Quality ${select("re-quality", s.quality, Object.entries(RE_LEVELS).map(([k, v]) => [k, `${v[0]} — ${v[1]}`]))}</label>
      <span class="linkish hint-text" id="re-more">${state.reencode.showOptions ? "Fewer options" : "More options"}</span>
      <div class="grow"></div>
      <span class="hint-text" title="${esc(f.path)}">ffmpeg ${esc(f.version)} · ${where}</span>
      ${state.reencode.running ? `<button class="btn small secondary danger" data-re="cancel-all">Stop everything</button>` : ""}
    </div>
    ${state.reencode.showOptions ? `
      <div class="actionbar re-options">
        <label>Format ${select("re-codec", s.codec, Object.entries(RE_CODECS).map(([k, v]) => [k, `${v[0]} — ${f.encoders.includes(v[1]) ? v[2] : "not in this ffmpeg"}`, !f.encoders.includes(v[1])]))}</label>
        <label>Speed ${select("re-speed", s.speed, Object.entries(RE_SPEEDS))}</label>
        <label>Resolution ${select("re-height", s.maxHeight, Object.entries(RE_HEIGHTS).sort((a, b) => (a[0] == 0 ? -1 : b[0] == 0 ? 1 : b[0] - a[0])))}</label>
        <span class="hint-text">Sound, subtitles and chapters are always copied untouched.</span>
      </div>` : ""}
    ${missing ? `
      <div class="re-banner warn">This ffmpeg cannot encode ${RE_CODECS[s.codec][0]} (it was built without <code>${RE_CODECS[s.codec][1]}</code>). Pick another format in <i>More options</i>${f.canDownload && f.source !== "app" ? `, or <span class="linkish" data-re="ffmpeg-install">let Subtitall download a complete ffmpeg</span>` : ""}.</div>` : ""}`;
}

function reTotalsHtml() {
  const done = reDone();
  if (!done.length) {
    return `<div class="re-banner">The original files are never touched: each new file is written next to its original, and you decide afterwards — <b>Keep new</b> deletes the original, <b>Undo</b> deletes the new file. Videos already in an efficient format are left unticked.</div>`;
  }
  const before = done.reduce((s, j) => s + j.originalBytes, 0), after = done.reduce((s, j) => s + j.newBytes, 0);
  const keepable = done.filter((j) => j.gainPercent > 0).length;
  return `
    <div class="re-banner totals">
      <span><b>${done.length}</b> file${done.length === 1 ? "" : "s"} re-encoded, waiting for your decision: ${formatSize(before)} → <b>${formatSize(after)}</b> ${gainHtml(before ? Math.round((1 - after / before) * 1000) / 10 : 0)} <span class="hint-text">Both versions are on your disk until you decide.</span></span>
      <span class="grow"></span>
      ${keepable ? `<button class="btn small" data-re="apply-all">Keep all new files</button>` : ""}
      <button class="btn small secondary" data-re="revert-all">Undo all</button>
    </div>`;
}

function renderReencodeView(main) {
  const r = state.reencode;
  if (!r.ffmpeg) {
    main.innerHTML = r.statusError
      ? `<div class="panel-empty warn-text">${esc(r.statusError)}</div>`
      : `<div class="panel-empty"><span class="spin"></span> Checking for ffmpeg…</div>`;
    if (!r.statusError)
      loadReencodeStatus().catch((err) => { r.statusError = err.message; }).finally(() => { if (state.view === "reencode") render(); });
    return;
  }
  if (r.ffmpeg.state !== "ready") { renderFfmpegGuide(main); return; }
  if (!state.items.length) {
    main.innerHTML = `<div class="dropzone"><div class="title">Nothing to re-encode yet</div><div>Add videos in the Files tab (or drop them here): Subtitall then tells you which ones can be made smaller without visible loss.</div></div>`;
    return;
  }
  if (r.files === null) {
    main.innerHTML = `<div class="panel-empty"><span class="spin"></span> Reading your videos…</div>`;
    loadReencodeFiles();
    return;
  }

  main.innerHTML = `${reOptionsHtml()}${reTotalsHtml()}${mediaGroups().map(reGroupHtml).join("")}`;

  const change = (id, key, convert = (v) => v) => {
    const el = main.querySelector(id);
    if (el) el.onchange = async () => {
      await saveReencodeSettings({ [key]: convert(el.value) });
      // What is worth re-encoding depends on the options (a smaller resolution always is).
      if (key !== "quality" && key !== "speed") { r.files = null; r.checks = {}; }
      if (r.trial && r.trial.state !== "running") post("/api/reencode/trial/cancel").catch(() => {});
      render();
    };
  };
  change("#re-quality", "quality");
  change("#re-codec", "codec");
  change("#re-speed", "speed");
  change("#re-height", "maxHeight", Number);
  main.querySelector("#re-more").onclick = () => { r.showOptions = !r.showOptions; render(); };

  main.onchange = (e) => {
    const id = e.target.dataset?.reCheck;
    if (!id) return;
    r.checks[id] = e.target.checked;
    const card = e.target.closest("[data-re-group]");
    const group = mediaGroups().find((g) => g.key === card.dataset.reGroup);
    if (group) card.querySelector(".re-group-actions").innerHTML = reGroupActionsHtml(group);
  };
  main.onclick = (e) => {
    const el = e.target.closest("[data-re]");
    if (el) reencodeAction(el.dataset.re, el.dataset).catch((err) => toast(err.message, "err"));
  };
}

// Redraws what one file's job changed, without rebuilding the whole view under the user's cursor.
function renderReencodeRow(itemId) {
  if (state.view !== "reencode") return;
  const cell = document.querySelector(`[data-re-status="${itemId}"]`);
  const item = state.items.find((i) => i.id === itemId);
  if (cell && item) cell.innerHTML = reStatusHtml(item);
}

function reReport(outcomes, done) {
  const failed = outcomes.filter((o) => !o.ok);
  const ok = outcomes.length - failed.length;
  if (ok) toast(done(ok), "ok");
  if (failed.length) toast(`${failed.length} file${failed.length === 1 ? "" : "s"} could not be handled: ${failed[0].error}`, "err", 8000);
}

async function reApply(ids) {
  if (!ids.length) return;
  const many = ids.length > 1;
  if (!confirm(`Keep the new file${many ? "s" : ""} and delete the ${many ? ids.length + " originals" : "original"} for good?\n\nThis cannot be undone.`)) return;
  reReport(await post("/api/reencode/apply", { itemIds: ids }), (n) => `${n} original${n === 1 ? "" : "s"} replaced by the smaller file`);
  closeReencodeModal();
}

async function reRevert(ids) {
  if (!ids.length) return;
  reReport(await post("/api/reencode/revert", { itemIds: ids }), (n) => `${n} new file${n === 1 ? "" : "s"} removed — the original${n === 1 ? " is" : "s are"} untouched`);
  closeReencodeModal();
}

async function reStart(ids, quality) {
  if (!ids.length) return;
  const { queued } = await post("/api/reencode/start", { itemIds: ids, quality: quality || null });
  if (queued) toast(`${queued} file${queued === 1 ? "" : "s"} queued — you can keep using the app`, "ok");
}

async function reencodeAction(action, data) {
  const r = state.reencode;
  const group = data.group != null ? mediaGroups().find((g) => g.key === data.group) : null;
  const keepable = (items) => reDone(items).filter((j) => j.gainPercent > 0).map((j) => j.itemId);
  switch (action) {
    case "start": {
      const ids = reSelected(group).map((i) => i.id);
      // Several files of one show: try on one first, unless that was already done for this group.
      if (ids.length > 1 && !r.tested[group.key]) openTrialModal(group.key, ids);
      else await reStart(ids, r.tested[group.key]);
      break;
    }
    case "trial": openTrialModal(group.key, reSelected(group).map((i) => i.id)); break;
    case "cancel": await post("/api/reencode/cancel", { itemId: data.id }); break;
    case "cancel-all": await post("/api/reencode/cancel", {}); break;
    case "apply": await reApply([data.id]); break;
    case "revert": await reRevert([data.id]); break;
    case "apply-group": await reApply(keepable(group.items)); break;
    case "revert-group": await reRevert(reDone(group.items).map((j) => j.itemId)); break;
    case "apply-all": await reApply(keepable(state.items)); break;
    case "revert-all": await reRevert(reDone().map((j) => j.itemId)); break;
    case "compare": openCompareModal(data.id); break;
    case "ffmpeg-install": await post("/api/reencode/ffmpeg/install"); break;
  }
}

// ---- Before / after viewer ---------------------------------------------------------------------------

// Two pictures of the same frame under a divider that follows the pointer.
function compareViewerHtml(sourceUrl, newUrl, label) {
  return `
    <div class="cmp" style="--split:50%">
      <img class="cmp-new" src="${esc(newUrl)}" alt="" draggable="false">
      <img class="cmp-old" src="${esc(sourceUrl)}" alt="" draggable="false">
      <div class="cmp-line"></div>
      <span class="cmp-tag left">Original</span>
      <span class="cmp-tag right">${esc(label)}</span>
    </div>
    <div class="hint-text cmp-help">Move the pointer over the picture to slide between the two versions · click to zoom in and out</div>`;
}

function bindCompareViewer(root) {
  const cmp = root.querySelector(".cmp");
  if (!cmp) return;
  cmp.onmousemove = (e) => {
    const box = cmp.getBoundingClientRect();
    const x = Math.min(1, Math.max(0, (e.clientX - box.left) / box.width));
    const y = Math.min(1, Math.max(0, (e.clientY - box.top) / box.height));
    cmp.style.setProperty("--split", `${x * 100}%`);
    cmp.style.setProperty("--ox", `${x * 100}%`);
    cmp.style.setProperty("--oy", `${y * 100}%`);
  };
  cmp.onclick = () => cmp.classList.toggle("zoom");
}

let reencodeModal = null; // { kind: "trial" | "compare", … } — what the modal shows right now

function closeReencodeModal() {
  if (!reencodeModal) return;
  reencodeModal = null;
  document.getElementById("modal-root").innerHTML = "";
}

function positionButtonsHtml(positions, current) {
  if (positions.length < 2) return "";
  return `<span class="cmp-positions">Moment: ${positions.map((p, i) =>
    `<button class="btn small ${i === current ? "" : "secondary"}" data-pos="${i}">${formatClock(p)}</button>`).join("")}</span>`;
}

// ---- Trial (short excerpts at each quality level) ---------------------------------------------------

async function openTrialModal(groupKey, itemIds) {
  if (!itemIds.length) return;
  const r = state.reencode;
  reencodeModal = { kind: "trial", groupKey, itemIds, level: state.reencode.tested[groupKey] || reSettings().quality, pos: 0 };
  // A finished trial of the same file is shown again instead of being redone.
  const reuse = r.trial && r.trial.itemId === itemIds[0] && r.trial.state !== "failed" && r.trial.codec === reSettings().codec;
  if (!reuse) {
    r.trial = { itemId: itemIds[0], fileName: state.items.find((i) => i.id === itemIds[0])?.fileName || "", state: "running", percent: 0, step: "Starting…", levels: [], positions: [] };
    renderReencodeModal();
    try {
      await post("/api/reencode/trial", { itemId: itemIds[0] });
    } catch (err) {
      r.trial = { ...r.trial, state: "failed", error: err.message };
    }
  }
  renderReencodeModal();
}

function trialModalHtml(m) {
  const t = state.reencode.trial;
  const n = m.itemIds.length;
  if (!t) return `<div class="modal-empty">The test was cancelled.</div>`;
  if (t.state === "failed") return `<div class="modal-empty warn-text">The test failed: ${esc(t.error)}</div>`;

  const levels = t.levels || [];
  const table = levels.length ? `
    <table class="cand-table re-levels">
      <thead><tr><th></th><th>Quality level</th><th>Estimated size</th><th>Space saved</th><th>Picture</th></tr></thead>
      <tbody>
        ${levels.map((l) => `
          <tr class="${l.quality === m.level ? "selected" : ""}" data-level="${l.quality}">
            <td><input type="radio" name="re-level" ${l.quality === m.level ? "checked" : ""} ${t.state === "done" ? "" : "disabled"}></td>
            <td><b>${RE_LEVELS[l.quality][0]}</b><div class="hint-text">${RE_LEVELS[l.quality][1]}</div></td>
            <td>${formatSize(t.originalBytes)} → <b>${formatSize(l.estimatedBytes)}</b></td>
            <td>${gainHtml(l.gainPercent)}</td>
            <td title="${l.ssim != null ? `SSIM ${l.ssim} (1 = identical)` : ""}">${esc(l.label || "not measured")}</td>
          </tr>`).join("")}
      </tbody>
    </table>` : "";

  if (t.state === "running") {
    return `
      <div class="re-trial-progress">
        <div class="re-progress big"><div style="width:${t.percent || 0}%"></div></div>
        <div class="re-install-line"><span><span class="spin"></span> <span id="re-trial-step">${esc(t.step || "Working…")}</span></span>
          <span class="hint-text">About a minute or two — short excerpts are encoded at three quality levels.</span></div>
      </div>
      ${table}
      <div class="identify-actions re-modal-actions">
        <span class="linkish hint-text" id="re-skip">Skip the test and re-encode ${n} file${n === 1 ? "" : "s"} now</span>
        <div class="grow"></div>
        <button class="btn secondary" id="re-trial-cancel">Cancel</button>
      </div>`;
  }

  const chosen = levels.find((l) => l.quality === m.level) || levels[0];
  const pos = Math.min(m.pos, Math.max(0, t.positions.length - 1));
  const frame = (name) => `/api/reencode/frame/${t.key}/${name}-${pos}.png`;
  return `
    <div class="hint-text re-trial-intro">Short excerpts of this file were encoded at three quality levels. Sizes are estimates for the whole file; the picture was measured against the original.</div>
    ${table}
    ${!levels.some((l) => l.gainPercent > 0)
      ? `<div class="warn-text re-trial-warning">Re-encoding would not make this file smaller at any level: it is already tightly compressed. Better leave these files as they are.</div>`
      : chosen && chosen.gainPercent <= 0
        ? `<div class="warn-text re-trial-warning">At this level the new file would be bigger than the original — pick a smaller one.</div>` : ""}
    ${chosen ? `
      <div class="cmp-bar">${positionButtonsHtml(t.positions, pos)}</div>
      ${compareViewerHtml(frame("source"), frame(chosen.quality), RE_LEVELS[chosen.quality][0])}` : ""}
    <div class="identify-actions re-modal-actions">
      <span class="linkish hint-text" id="re-trial-again">Run the test again</span>
      <div class="grow"></div>
      <button class="btn secondary" id="re-trial-close">Close</button>
      <button class="btn ${chosen && chosen.gainPercent <= 0 ? "secondary" : ""}" id="re-trial-go" ${chosen ? "" : "disabled"}>Re-encode ${n} file${n === 1 ? "" : "s"} — ${chosen ? RE_LEVELS[chosen.quality][0] : ""}</button>
    </div>`;
}

// ---- Comparison of a finished file ---------------------------------------------------------------------

async function openCompareModal(itemId) {
  reencodeModal = { kind: "compare", itemId, pos: 0, frames: null, error: null };
  const mine = reencodeModal;
  renderReencodeModal();
  try {
    mine.frames = await get(`/api/reencode/compare?itemId=${encodeURIComponent(itemId)}`);
  } catch (err) {
    mine.error = err.message;
  }
  if (reencodeModal === mine) renderReencodeModal();
}

function compareModalHtml(m) {
  const job = state.reencode.jobs[m.itemId];
  if (!job || job.state !== "done") return `<div class="modal-empty">Nothing to compare any more.</div>`;
  const worse = job.gainPercent <= 0;
  const summary = `
    <div class="re-compare-summary">
      <div><span class="hint-text">Original</span><b>${formatSize(job.originalBytes)}</b></div>
      <div class="arrow">→</div>
      <div><span class="hint-text">Re-encoded</span><b>${formatSize(job.newBytes)}</b></div>
      <div><span class="hint-text">Space saved</span>${gainHtml(job.gainPercent)} <span class="hint-text">${formatSize(Math.abs(job.originalBytes - job.newBytes))}</span></div>
      <div><span class="hint-text">Picture</span><b title="${job.ssim != null ? `SSIM ${job.ssim} (1 = identical)` : ""}">${esc(job.quality || "not measured")}</b></div>
    </div>`;
  let viewer;
  if (m.error) viewer = `<div class="modal-empty warn-text">${esc(m.error)}</div>`;
  else if (!m.frames) viewer = `<div class="modal-empty"><span class="spin"></span> Taking the same pictures out of both files…</div>`;
  else {
    const url = (side) => `/api/reencode/frame/${m.frames.key}/${side}-${m.pos}.png?v=${m.frames.stamp}`;
    viewer = `<div class="cmp-bar">${positionButtonsHtml(m.frames.positions, m.pos)}</div>${compareViewerHtml(url("source"), url("new"), "Re-encoded")}`;
  }
  return `
    ${summary}
    ${viewer}
    <div class="identify-actions re-modal-actions">
      <span class="hint-text">Both files are on your disk until you decide.</span>
      <div class="grow"></div>
      <button class="btn secondary" id="re-cmp-revert" title="Delete the new file">Undo — keep the original</button>
      ${worse ? "" : `<button class="btn" id="re-cmp-apply" title="Delete the original">Keep the new file</button>`}
    </div>`;
}

function renderReencodeModal() {
  const m = reencodeModal;
  if (!m) return;
  const root = document.getElementById("modal-root");
  const title = m.kind === "trial"
    ? `Test on “${state.reencode.trial?.fileName || ""}”`
    : `Compare “${state.items.find((i) => i.id === m.itemId)?.fileName || ""}”`;
  root.innerHTML = `
    <div class="modal-backdrop">
      <div class="modal wide">
        <div class="modal-head">
          <span class="title">${esc(title)}</span>
          <button class="icon-btn" id="modal-close" title="Close">&#10005;</button>
        </div>
        <div class="modal-body re-modal-body">${m.kind === "trial" ? trialModalHtml(m) : compareModalHtml(m)}</div>
      </div>
    </div>`;

  const on = (selector, handler) => { const el = root.querySelector(selector); if (el) el.onclick = handler; };
  const guard = (fn) => () => Promise.resolve(fn()).catch((err) => toast(err.message, "err"));
  on("#modal-close", closeReencodeModal);
  root.querySelector(".modal-backdrop").onmousedown = (e) => { if (e.target.classList.contains("modal-backdrop")) closeReencodeModal(); };
  root.querySelectorAll("[data-pos]").forEach((btn) => { btn.onclick = () => { m.pos = Number(btn.dataset.pos); renderReencodeModal(); }; });
  bindCompareViewer(root);

  if (m.kind === "trial") {
    const start = async (quality) => {
      const { groupKey, itemIds } = m;
      if (quality) state.reencode.tested[groupKey] = quality;
      closeReencodeModal();
      await reStart(itemIds, quality);
    };
    root.querySelectorAll("[data-level]").forEach((row) => {
      row.onclick = () => { if (state.reencode.trial?.state === "done") { m.level = row.dataset.level; renderReencodeModal(); } };
    });
    on("#re-trial-cancel", guard(async () => { closeReencodeModal(); await post("/api/reencode/trial/cancel"); }));
    on("#re-trial-close", closeReencodeModal);
    on("#re-skip", guard(async () => { await post("/api/reencode/trial/cancel"); await start(null); }));
    on("#re-trial-go", guard(() => start(m.level)));
    on("#re-trial-again", guard(async () => {
      await post("/api/reencode/trial/cancel");
      state.reencode.trial = null;
      await openTrialModal(m.groupKey, m.itemIds);
    }));
  } else {
    on("#re-cmp-apply", guard(() => reApply([m.itemId])));
    on("#re-cmp-revert", guard(() => reRevert([m.itemId])));
  }
}

// What the top bar says while files are being re-encoded.
function reencodeProgressText() {
  const jobs = Object.values(state.reencode.jobs);
  const active = jobs.find((j) => j.state === "encoding" || j.state === "checking");
  const waiting = jobs.filter((j) => j.state === "queued").length;
  if (!active && !waiting) return "";
  return `Re-encoding${active?.state === "encoding" ? ` ${Math.floor(active.percent)}%` : ""}${waiting ? ` · ${waiting} waiting` : ""}`;
}

// ---------- Sync view ------------------------------------------------------------------------------------
// Compares the subtitles of every video with its audio: which ones follow the speech, and how to fix the others.
// Videos are listed by show, like in the Re-encode tab; a video opens to show its subtitles one by one.

const SYNC_BADGES = {
  inSync: { text: "In sync", cls: "ok" },
  shift: { text: "Needs a shift", cls: "warn" },
  rescale: { text: "Needs stretching", cls: "warn" },
  differentCut: { text: "Another cut", cls: "bad" },
  unclear: { text: "Can't tell", cls: "dim" },
  unreadable: { text: "Nothing to compare", cls: "dim" },
};

const isSubtitlePath = (path) => /\.(srt|vtt|ass|ssa)$/i.test(path);
const percentText = (x) => `${Math.round((x || 0) * 100)}%`;
const signed = (seconds) => `${seconds > 0 ? "+" : seconds < 0 ? "−" : ""}${Math.abs(seconds).toFixed(Math.abs(seconds) < 10 ? 1 : 0)} s`;
const plural = (n, word) => `${n} ${word}${n === 1 ? "" : "s"}`;

// Guards against an older answer landing after a newer one.
let syncLoadToken = 0;

// Every video with what the server knows about its subtitles (null until asked).
async function loadSyncAll() {
  const s = state.sync;
  const token = ++syncLoadToken;
  try {
    const list = await get("/api/sync");
    if (token !== syncLoadToken) return;
    s.error = null;
    s.all = Object.fromEntries(list.map((d) => [d.itemId, d]));
    for (const d of list) {
      if (d.state === "running" || d.state === "queued") s.busy[d.itemId] = { percent: d.percent, step: d.step };
      else delete s.busy[d.itemId];
    }
  } catch (err) {
    if (token === syncLoadToken) { s.error = err.message; s.all = {}; }
  }
  if (state.view === "sync") render();
}

const loadSyncAllDebounced = debounce(() => loadSyncAll().catch(console.error), 400);

function syncProgressText() {
  const busy = Object.values(state.sync.busy);
  if (!busy.length) return "";
  const running = busy.find((b) => b.step && b.percent > 0) || busy[0];
  const waiting = busy.length - 1;
  return `Listening ${Math.floor(running.percent || 0)}%${waiting ? ` · ${waiting} waiting` : ""}`;
}

// What a video's row says: a kind, and the numbers behind it.
function syncSummary(d) {
  if (!d) return { kind: "loading" };
  if (d.state === "running") return { kind: "running", percent: d.percent, step: d.step };
  if (d.state === "queued") return { kind: "queued" };
  if (d.state === "failed") return { kind: "failed", error: d.error };
  if (!d.candidates.length) return { kind: "none" };
  // The subtitles that sit next to the video are the ones that count; files added to compare come second.
  const own = d.candidates.filter((c) => c.source === "folder");
  const pool = own.length ? own : d.candidates;
  if (pool.some((c) => !c.analyzed)) return { kind: "unchecked", total: pool.length };
  const inSync = pool.filter((c) => c.analysis?.verdict === "inSync").length;
  const fixable = pool.filter((c) => c.action === "fix").length;
  const trouble = pool.length - inSync - fixable;
  return { kind: fixable ? "fix" : trouble ? "trouble" : "ok", inSync, fixable, trouble, total: pool.length };
}

const syncWaiting = (item) => ["running", "queued"].includes(state.sync.all?.[item.id]?.state);
const syncCheckable = (item) => {
  const k = syncSummary(state.sync.all?.[item.id]).kind;
  return k === "unchecked" || k === "failed";
};

function syncStatusHtml(item) {
  const d = state.sync.all?.[item.id];
  const s = syncSummary(d);
  switch (s.kind) {
    case "loading": return `<span class="hint-text">…</span>`;
    case "running":
      return `<span class="re-progress"><div data-sync-bar="${item.id}" style="width:${s.percent || 0}%"></div></span>
        <span class="re-progress-text" data-sync-step="${item.id}">${esc(s.step || "")}</span>
        <button class="icon-btn" data-sync="stop" data-id="${item.id}" title="Stop">&#10005;</button>`;
    case "queued":
      return `<span class="hint-text">Waiting…</span> <button class="icon-btn" data-sync="stop" data-id="${item.id}" title="Remove from the queue">&#10005;</button>`;
    case "failed":
      return `<span class="sync-badge bad" title="${esc(s.error || "")}">Failed</span> <span class="hint-text">${esc(s.error || "")}</span>
        <button class="btn small secondary" data-sync="check" data-id="${item.id}">Try again</button>`;
    case "none": return `<span class="hint-text">No subtitle next to this video — open it to add some</span>`;
    case "unchecked":
      return `<span class="hint-text">${plural(s.total, "subtitle")}, not checked yet</span>
        <button class="btn small secondary" data-sync="check" data-id="${item.id}">Check</button>`;
    case "ok":
      return `<span class="sync-badge ok">In sync</span> <span class="hint-text">${s.total === 1 ? "" : `all ${s.total}`}</span>`;
    case "fix":
      return `<span class="sync-badge warn">${s.fixable} to fix</span>
        <span class="hint-text">${[s.inSync ? `${s.inSync} in sync` : "", s.trouble ? `${s.trouble} can't be trusted` : ""].filter(Boolean).join(" · ")}</span>`;
    default:
      return `<span class="sync-badge dim">Can't tell</span> <span class="hint-text">${s.inSync ? `${s.inSync} in sync · ` : ""}${plural(s.trouble, "subtitle")} to look at</span>`;
  }
}

function syncGroupSummary(group) {
  const kinds = group.items.map((i) => syncSummary(state.sync.all?.[i.id]));
  const count = (k) => kinds.filter((x) => x.kind === k).length;
  const parts = [plural(group.items.length, "file")];
  if (count("ok")) parts.push(`${count("ok")} in sync`);
  if (count("fix")) parts.push(`${count("fix")} to fix`);
  if (count("trouble")) parts.push(`${count("trouble")} to look at`);
  if (count("unchecked")) parts.push(`${count("unchecked")} not checked`);
  if (count("none")) parts.push(`${count("none")} without subtitle`);
  return parts.join(" · ");
}

function syncGroupActionsHtml(group) {
  const todo = group.items.filter(syncCheckable);
  return todo.length
    ? `<button class="btn small" data-sync="check-group" data-group="${esc(group.key)}">Check ${plural(todo.length, "video")}</button>`
    : "";
}

function syncVideoRowHtml(item) {
  const s = state.sync;
  const d = s.all?.[item.id];
  const open = s.itemId === item.id;
  const langs = (d?.candidates || []).filter((c) => c.source === "folder" && c.language)
    .map((c) => `<span class="sync-chip">${esc(c.language)}</span>`).join(" ");
  return `
    <tr class="sync-vrow${open ? " open" : ""}" data-sync="toggle" data-id="${item.id}">
      <td title="${esc(item.path)}"><span class="sync-caret">${open ? "▾" : "▸"}</span> ${esc(item.fileName)}</td>
      <td class="col-langs">${langs}</td>
      <td class="col-status" data-sync-status="${item.id}">${syncStatusHtml(item)}</td>
    </tr>
    ${open ? `<tr class="sync-drow"><td colspan="3">${syncDetailHtml(item, d)}</td></tr>` : ""}`;
}

function syncGroupHtml(group) {
  const name = group.kind === "title" ? `“${esc(group.name)}”` : esc(group.name);
  return `
    <div class="card re-group" data-sync-group="${esc(group.key)}">
      <div class="group-head">
        <div class="meta">
          <div class="name">${name}</div>
          <div class="sub">${esc(syncGroupSummary(group))}</div>
        </div>
        <span class="re-group-actions">${syncGroupActionsHtml(group)}</span>
      </div>
      <table class="file-table re-table">
        <thead><tr><th>File</th><th class="col-langs">Subtitles</th><th class="col-status">Sync</th></tr></thead>
        <tbody>${group.items.map(syncVideoRowHtml).join("")}</tbody>
      </table>
    </div>`;
}

// ---- One video, opened ------------------------------------------------------------------------------

function syncSummaryHtml(d) {
  if (d.state === "running" || d.state === "queued") {
    return `
      <div class="re-banner sync-running">
        <div class="re-progress big"><div id="sync-bar" style="width:${d.percent || 0}%"></div></div>
        <div class="re-install-line">
          <span><span class="spin"></span> <span id="sync-step">${esc(d.step || "Working…")}</span></span>
          <button class="btn small secondary" data-sync="cancel">Stop</button>
        </div>
      </div>`;
  }
  if (d.state === "failed")
    return `<div class="re-banner warn">${esc(d.error || "The comparison failed.")} <span class="linkish" data-sync="analyze">Try again</span></div>`;
  if (!d.candidates.length)
    return `<div class="re-banner">No subtitle file sits next to this video. Add the subtitles you want to compare — they can come from anywhere.</div>`;
  if (d.candidates.some((c) => !c.analyzed))
    return `<div class="re-banner">Not compared yet. <span class="linkish" data-sync="analyze">Compare now</span></div>`;

  const best = d.candidates.filter((c) => c.best);
  const analysed = d.candidates.filter((c) => c.analysis).length;
  const minutes = Math.round((d.durationSeconds || 0) / 60);
  const facts = d.durationSeconds
    ? `<span class="hint-text">Compared ${plural(analysed, "subtitle")} with ${minutes} min of audio, speech heard ${d.speechPercent}% of the time.</span>`
    : "";
  if (!best.length)
    return `<div class="re-banner warn">None of these subtitles matches this video's audio well enough to trust. Try other subtitle files. ${facts}</div>`;
  const top = best[0];
  const a = top.analysis;
  const what = a.verdict === "inSync" ? "it follows the speech" : `${esc(a.headline.charAt(0).toLowerCase() + a.headline.slice(1))}. ${esc(a.advice)}`;
  return `<div class="re-banner totals"><span><b>${esc(top.name)}</b> is the best match: ${what}. ${facts}</span></div>`;
}

function syncRowHtml(c, rank) {
  const s = state.sync;
  const a = c.analysis;
  const badge = c.error
    ? { text: "Can't read", cls: "bad" }
    : a ? SYNC_BADGES[a.verdict] : { text: "Waiting…", cls: "dim" };
  const lang = c.language ? `<span class="sync-chip">${esc(c.language)}</span>` : "";
  const added = c.source === "added" ? `<span class="sync-chip alt" title="${esc(c.folder || "")}">added</span>` : "";

  let note = "";
  if (c.error) note = esc(c.error);
  else if (a) note = esc(a.headline) + (a.advice ? ` — <b>${esc(a.advice)}</b>` : "");

  let meter = "";
  if (a && a.verdict !== "unreadable") {
    const shown = a.fix ? a.fixedScore : a.currentScore;
    const coverage = a.fix ? `${percentText(a.currentCoverage)} → <b>${percentText(a.fixedCoverage)}</b>` : `<b>${percentText(a.fixedCoverage)}</b>`;
    meter = `
      <div class="sync-meter" title="How well the moments the subtitles are shown match the moments people speak (${a.speechCues} spoken subtitles)">
        <div class="sync-bar"><div style="width:${Math.max(0, Math.min(100, shown * 125))}%"></div></div>
        <span class="hint-text">Speech under the subtitles: ${coverage}${a.confidence ? ` · confidence ${esc(a.confidence)}` : ""}</span>
      </div>`;
  }

  const actions = [];
  if (c.action === "fix")
    actions.push(`<button class="btn small" data-sync="apply" data-path="${esc(c.path)}" title="Writes the corrected subtitle. ${c.replaces ? `It replaces ${esc(c.replaces)}, which is kept as a .bak file.` : "The original is kept as a .bak file."}">${c.source === "added" ? (c.replaces ? "Fix and replace " + esc(c.replaces) : "Fix and save next to the video") : "Fix it"}</button>`);
  if (c.action === "use")
    actions.push(`<button class="btn small" data-sync="apply" data-path="${esc(c.path)}" title="Copies it next to the video under its name${c.replaces ? `, replacing ${esc(c.replaces)} (kept as a .bak file)` : ""}">${c.replaces ? "Use instead of " + esc(c.replaces) : "Use for this video"}</button>`);
  if (c.canUndo)
    actions.push(`<button class="btn small secondary" data-sync="undo" data-path="${esc(c.path)}" title="Puts the original back">Undo</button>`);
  if (c.source === "added")
    actions.push(`<button class="icon-btn" data-sync="remove" data-path="${esc(c.path)}" title="Remove from the comparison">&#10005;</button>`);
  if (a && a.segments?.length)
    actions.push(`<button class="icon-btn" data-sync="details" data-path="${esc(c.path)}" title="Where it is off, along the video">${s.open[c.path] ? "▴" : "▾"}</button>`);

  return `
    <div class="sync-row${c.best ? " best" : ""}">
      <div class="sync-rank">${a && rank ? `#${rank}` : ""}</div>
      <div class="sync-main">
        <div class="sync-file"><span class="sync-name" title="${esc(c.path)}">${esc(c.name)}</span> ${lang} ${added} ${c.best ? `<span class="badge-rec">best match</span>` : ""}</div>
        <div class="sync-note">${note}</div>
        ${meter}
      </div>
      <div class="sync-verdict"><span class="sync-badge ${badge.cls}">${badge.text}</span></div>
      <div class="sync-actions">${actions.join("")}</div>
      ${s.open[c.path] && a ? syncDetailsHtml(a) : ""}
    </div>`;
}

function syncDetailsHtml(a) {
  const segments = a.segments.map((seg) => {
    const text = !seg.valid ? "no clear match" : `${signed(seg.shift)}`;
    return `<span class="sync-seg${seg.outlier ? " out" : ""}${seg.valid ? "" : " none"}" title="Match ${percentText(seg.score)}">
      <span class="hint-text">${formatClock(seg.time)}</span> ${text}</span>`;
  }).join("");
  const fix = a.fix
    ? `Fix: <b>× ${a.fix.scale.toFixed(5)}</b> then <b>${signed(a.fix.offset)}</b>${a.fix.rate ? ` (${esc(a.fix.rate)})` : ""}.`
    : "No fix needed.";
  return `
    <div class="sync-details">
      <div class="hint-text">Shift each part of the video needs, from start to end (positive = the subtitle appears too early):</div>
      <div class="sync-segs">${segments}</div>
      <div class="hint-text">${fix} Off by ${signed(a.startError)} at the start and ${signed(a.endError)} at the end. Overall match ${percentText(a.currentScore)}${a.fix ? ` → ${percentText(a.fixedScore)}` : ""}.</div>
    </div>`;
}

function syncDetailHtml(item, d) {
  if (!d) return `<div class="panel-empty"><span class="spin"></span> Looking at this video…</div>`;
  const tracks = d.audioTracks?.length > 1
    ? `<label>Audio <select class="input" id="sync-audio">${d.audioTracks.map((t) => `<option value="${t.index}"${t.index === d.audioIndex ? " selected" : ""}>${esc(t.label)}</option>`).join("")}</select></label>`
    : "";
  const busy = d.state === "running" || d.state === "queued";
  let rank = 0;
  const rows = d.candidates.map((c) => syncRowHtml(c, c.analysis && c.analysis.verdict !== "unreadable" ? ++rank : 0)).join("");
  return `
    <div class="sync-detail">
      <div class="re-options sync-head">
        ${tracks}
        <span class="grow"></span>
        <button class="btn small secondary" data-sync="add">Add subtitle files…</button>
        <button class="btn small secondary" data-sync="analyze"${busy ? " disabled" : ""}>Compare again</button>
      </div>
      ${syncSummaryHtml(d)}
      ${d.candidates.length ? `<div class="card sync-list">${rows}</div>` : ""}
    </div>`;
}

// ---- The tab ------------------------------------------------------------------------------------------

function renderSyncView(main) {
  const s = state.sync;
  const r = state.reencode;
  if (!r.ffmpeg) {
    main.innerHTML = r.statusError
      ? `<div class="panel-empty warn-text">${esc(r.statusError)}</div>`
      : `<div class="panel-empty"><span class="spin"></span> Checking for ffmpeg…</div>`;
    if (!r.statusError)
      loadReencodeStatus().catch((err) => { r.statusError = err.message; }).finally(() => { if (state.view === "sync") render(); });
    return;
  }
  if (r.ffmpeg.state !== "ready") { renderFfmpegGuide(main, "Listening to your videos"); return; }
  if (!state.items.length) {
    main.innerHTML = `<div class="dropzone"><div class="title">No video to check yet</div><div>Add videos in the Files tab (or drop them here): Subtitall then tells you whether their subtitles follow the speech.</div></div>`;
    return;
  }
  if (s.all === null) {
    main.innerHTML = s.error
      ? `<div class="panel-empty warn-text">${esc(s.error)}</div>`
      : `<div class="panel-empty"><span class="spin"></span> Looking at your videos…</div>`;
    loadSyncAll();
    return;
  }

  const todo = state.items.filter(syncCheckable);
  const anyBusy = state.items.some(syncWaiting);
  main.innerHTML = `
    <div class="actionbar re-options">
      <span class="hint-text">Listens to each video's audio, then checks its subtitles against the speech. Open a video to see them one by one.</span>
      <div class="grow"></div>
      <span class="hint-text" title="${esc(r.ffmpeg.path)}">ffmpeg ${esc(r.ffmpeg.version)}</span>
      ${anyBusy ? `<button class="btn small secondary danger" data-sync="stop-all">Stop everything</button>` : ""}
      ${todo.length ? `<button class="btn small" data-sync="check-all">Check ${todo.length === state.items.length ? "all videos" : plural(todo.length, "video")}</button>` : ""}
    </div>
    <details class="sync-how"><summary>How does this work?</summary>
      <p>Subtitall listens to each video's audio with ffmpeg (a few seconds per episode) and finds where people speak. It then slides each subtitle along that speech until the two agree — one shift for the whole file, then the same check on each part of the video, to tell a plain shift from a frame-rate mismatch (the subtitle drifts) or from a subtitle made for another cut.</p>
      <p>No file leaves your computer. Fixing a subtitle writes the corrected file and keeps the original next to it as <code>.bak</code>; <i>Undo</i> puts it back. Subtitles the app cannot tell anything about are marked <i>Can't tell</i> rather than guessed.</p>
    </details>
    ${s.error ? `<div class="re-banner warn">${esc(s.error)}</div>` : ""}
    ${mediaGroups().map(syncGroupHtml).join("")}`;

  main.onchange = (e) => {
    if (e.target.id === "sync-audio")
      post("/api/sync/analyze", { itemId: s.itemId, audioIndex: Number(e.target.value) }).catch((err) => toast(err.message, "err"));
  };
  main.onclick = (e) => {
    const el = e.target.closest("[data-sync]");
    if (el) syncAction(el.dataset.sync, el.dataset).catch((err) => toast(err.message, "err"));
  };
}

async function syncAction(action, data) {
  const s = state.sync;
  const itemId = s.itemId;
  switch (action) {
    case "toggle": {
      s.itemId = s.itemId === data.id ? null : data.id;
      s.open = {};
      render();
      // Opening a video nobody has looked at yet: compare its subtitles.
      const d = s.all?.[data.id];
      if (s.itemId && d && d.state === "idle" && d.candidates.some((c) => !c.analyzed) && !s.started[data.id]) {
        s.started[data.id] = true;
        await post("/api/sync/analyze", { itemId: data.id });
      }
      break;
    }
    case "check": await post("/api/sync/analyze-many", { itemIds: [data.id] }); break;
    case "check-all": await post("/api/sync/analyze-many", { itemIds: state.items.filter(syncCheckable).map((i) => i.id) }); break;
    case "check-group": {
      const group = mediaGroups().find((g) => g.key === data.group);
      if (group) await post("/api/sync/analyze-many", { itemIds: group.items.filter(syncCheckable).map((i) => i.id) });
      break;
    }
    case "stop": await post("/api/sync/cancel", { itemId: data.id }); break;
    case "stop-all": await post("/api/sync/cancel", {}); break;
    case "analyze": await post("/api/sync/analyze", { itemId }); break;
    case "cancel": await post("/api/sync/cancel", { itemId }); break;
    case "details": s.open[data.path] = !s.open[data.path]; render(); break;
    case "remove": await post("/api/sync/remove", { itemId, path: data.path }); break;
    case "add": await syncBrowse(); break;
    case "apply": {
      const result = await post("/api/sync/apply", { itemId, path: data.path });
      toast(result.message, "ok", 6000);
      break;
    }
    case "undo": {
      const result = await post("/api/sync/undo", { itemId, path: data.path });
      toast(result.message, "ok");
      break;
    }
  }
}

async function syncBrowse() {
  const itemId = state.sync.itemId;
  let added;
  if (state.meta.isElectron) {
    ({ added } = await post("/api/sync/browse", { itemId }));
  } else {
    const path = prompt("Desktop dialogs are unavailable in the browser.\nEnter the path of a subtitle file or of a folder of subtitles:");
    if (!path) return;
    ({ added } = await post("/api/sync/add", { itemId, paths: [path] }));
  }
  if (added > 0) toast(`Added ${plural(added, "subtitle")} to compare`, "ok");
}

// ---------- Init ------------------------------------------------------------------------------------

function setupNavigation() {
  document.querySelectorAll(".tab").forEach((tab) => {
    tab.onclick = () => {
      state.view = tab.dataset.view;
      // The folder may have changed since the last visit (a subtitle downloaded, one removed).
      if (state.view === "sync") Object.assign(state.sync, { all: null, error: null, started: {} });
      render();
    };
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
  await loadReencodeStatus().catch(() => { /* asked again when the tab opens */ });
  connectEvents();
  render();
})();
