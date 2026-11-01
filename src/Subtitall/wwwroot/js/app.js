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
  const [meta, settings, items] = await Promise.all([
    get("/api/meta"), get("/api/settings"), get("/api/library"),
  ]);
  state.meta = meta;
  state.settings = settings;
  state.items = items;
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
        if (type === "settings-changed") get("/api/settings").then((s) => { state.settings = s; render(); });
        loadLibraryDebounced();
        if (state.view === "rename" && state.proposals !== null && !state.renameBusy) loadProposalsDebounced();
        break;
      case "batch-started":
        state.searching = true;
        state.progress = { done: 0, total: data.total };
        state.langState = {};
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
      case "update-downloaded":
        toast(`Update v${data.version} downloaded — it will install when you close the app`, "ok", 10000);
        break;
      case "batch-finished": {
        state.searching = false;
        const parts = [`${data.downloaded} downloaded`];
        if (data.skipped) parts.push(`${data.skipped} already there`);
        if (data.failed) parts.push(`${data.failed} not found`);
        toast(`Search ${data.cancelled ? "cancelled" : "finished"} — ${parts.join(", ")}`,
          data.downloaded > 0 ? "ok" : (data.failed > 0 ? "err" : "info"), 6000);
        loadLibraryDebounced();
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
        : `<span class="hint-text">Click a language chip to pick a subtitle manually</span>`}
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
    let cls = "", title = `Search ${lang} subtitles for this file`;
    if (st === "searching") { cls = "searching"; title = "Searching…"; }
    else if (st?.startsWith("failed:")) { cls = "failed"; title = st.slice(7); }
    else if (item.subtitles?.[lang]) { cls = "have"; title = `${lang} subtitle present — click to replace`; }
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

  let candidates;
  try {
    candidates = await get(`/api/subtitles/candidates?itemId=${encodeURIComponent(itemId)}&language=${encodeURIComponent(language)}`);
  } catch (err) {
    if (token !== modalToken) return; // modal was closed/replaced meanwhile
    const body = root.querySelector(".modal-body");
    if (body) body.innerHTML = `<div class="modal-empty">Search failed: ${esc(err.message)}</div>`;
    return;
  }

  if (token !== modalToken) return; // modal was closed/replaced meanwhile
  const body = root.querySelector(".modal-body");
  if (!body) return;
  if (!candidates.length) {
    body.innerHTML = `<div class="modal-empty">No subtitles found for this file in “${esc(language)}”.</div>`;
    return;
  }

  body.innerHTML = `
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

// ---------- Settings view --------------------------------------------------------------------------

function renderSettingsView(main) {
  const s = state.settings;
  if (!s) { main.innerHTML = ""; return; }
  const selected = new Set(s.languages);

  main.innerHTML = `
    <div class="settings-page card">
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
        <div class="desc">Searches run on every enabled provider; the best-scored subtitle wins.</div>

        <div class="provider-card ${s.gestdown.enabled ? "enabled" : ""}" id="card-gestdown">
          <div class="head">
            <label class="toggle"><input type="checkbox" id="gestdown-enabled" ${s.gestdown.enabled ? "checked" : ""}><span class="slider"></span></label>
            <span class="name">Gestdown</span>
            <span class="badge">no key needed</span>
            <span class="note">TV shows (Addic7ed mirror)</span>
          </div>
        </div>

        <div class="provider-card ${s.openSubtitles.enabled ? "enabled" : ""}" id="card-opensubtitles">
          <div class="head">
            <label class="toggle"><input type="checkbox" id="os-enabled" ${s.openSubtitles.enabled ? "checked" : ""}><span class="slider"></span></label>
            <span class="name">OpenSubtitles</span>
            <span class="note">movies + TV — <a href="https://www.opensubtitles.com/en/consumers" target="_blank">get a free API key</a></span>
          </div>
          <div class="fields">
            <div class="settings-row"><label>API key</label><input class="input" id="os-key" value="${esc(s.openSubtitles.apiKey)}" placeholder="${state.meta?.builtInKeys?.openSubtitles ? "Using built-in key — paste yours to override" : "Required"}"></div>
            <div class="settings-row"><label>Username</label><input class="input" id="os-user" value="${esc(s.openSubtitles.username)}" placeholder="Optional — raises download quota"></div>
            <div class="settings-row"><label>Password</label><input class="input" id="os-pass" type="password" value="${esc(s.openSubtitles.password)}" placeholder="Optional"></div>
          </div>
        </div>

        <div class="provider-card ${s.subDl.enabled ? "enabled" : ""}" id="card-subdl">
          <div class="head">
            <label class="toggle"><input type="checkbox" id="subdl-enabled" ${s.subDl.enabled ? "checked" : ""}><span class="slider"></span></label>
            <span class="name">SubDL</span>
            <span class="note">movies + TV — <a href="https://subdl.com/panel/api" target="_blank">get a free API key</a></span>
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
        <button class="btn" id="save-settings">Save settings</button>
      </div>
    </div>`;

  ["gestdown", "opensubtitles", "subdl", "subsource", "tmdb"].forEach((key) => {
    const card = main.querySelector(`#card-${key}`);
    const box = card.querySelector("input[type=checkbox]");
    box.onchange = () => card.classList.toggle("enabled", box.checked);
  });

  main.querySelector("#save-settings").onclick = async () => {
    const languages = [...main.querySelectorAll("[data-lang]")]
      .filter((box) => box.checked)
      .map((box) => box.dataset.lang);
    if (!languages.length) {
      toast("Select at least one language", "err");
      return;
    }
    const updated = {
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
    try {
      state.settings = await put("/api/settings", updated);
      toast("Settings saved", "ok");
    } catch (err) {
      toast(`Could not save: ${err.message}`, "err");
    }
  };
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
