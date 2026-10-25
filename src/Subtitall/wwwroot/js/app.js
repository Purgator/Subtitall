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
  showQuery: "",
  showResults: [],
  selectedShow: null,
  proposals: null,
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
    s.openSubtitles?.enabled && s.openSubtitles?.apiKey ? "OpenSubtitles" : null,
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

  const p = item.parsed || {};
  let detected = esc(p.title || "");
  if (p.season != null && p.episode != null) {
    const ep = `S${String(p.season).padStart(2, "0")}E${String(p.episode).padStart(2, "0")}${p.endEpisode ? "-E" + String(p.endEpisode).padStart(2, "0") : ""}`;
    detected += ` <span class="ep">${ep}</span>`;
  } else if (p.absoluteEpisode != null) {
    detected += ` <span class="ep">#${p.absoluteEpisode}</span>`;
  } else if (p.year) {
    detected += ` <span class="ep">(${p.year})</span>`;
  }

  return `
    <tr data-row="${item.id}">
      <td class="col-langs">${chips}</td>
      <td title="${esc(item.path)}">${esc(item.fileName)}</td>
      <td class="col-parsed"><span class="parsed-tag">${detected}</span></td>
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

// ---------- Rename view -------------------------------------------------------------------------

// Guards against out-of-order proposal responses when switching shows quickly.
let selectShowToken = 0;

async function selectShow(show) {
  if (!show) return;
  const token = ++selectShowToken;
  state.selectedShow = show;
  state.proposals = null;
  render();
  try {
    const proposals = await post("/api/rename/proposals", { showId: show.id, showName: show.name, source: show.source });
    if (token !== selectShowToken) return; // a newer selection superseded this one
    state.proposals = proposals;
    state.proposalChecks = {};
    for (const proposal of proposals)
      state.proposalChecks[proposal.itemId] = proposal.canApply;
  } catch (err) {
    if (token !== selectShowToken) return;
    state.proposals = [];
    toast(`Could not load episodes: ${err.message}`, "err");
  }
  render();
}

function renderRenameView(main) {
  if (!state.items.length) {
    main.innerHTML = `<div class="dropzone"><div class="title">Nothing to rename yet</div><div>Add some episodes in the Files tab first.</div></div>`;
    return;
  }

  main.innerHTML = `
    <div class="rename-grid">
      <div class="card">
        <div class="panel-title">1 · Find the show</div>
        <div class="panel-body">
          <input class="input" id="show-search" placeholder="Search on TVMaze / TMDB…" value="${esc(state.showQuery)}">
          <div id="show-results" style="margin-top:8px"></div>
        </div>
      </div>
      <div class="card" id="rename-right"></div>
    </div>`;

  const input = main.querySelector("#show-search");
  input.oninput = debounce(async () => {
    state.showQuery = input.value.trim();
    if (state.showQuery.length < 2) { state.showResults = []; renderShowResults(); return; }
    try {
      state.showResults = await get(`/api/rename/shows?q=${encodeURIComponent(state.showQuery)}`);
    } catch { state.showResults = []; }
    renderShowResults();
  }, 400);

  renderShowResults();
  renderRenamePanel();
}

function renderShowResults() {
  const root = document.getElementById("show-results");
  if (!root) return;
  if (!state.showResults.length) {
    root.innerHTML = `<div class="hint-text" style="padding:6px 2px">${state.showQuery.length >= 2 ? "No results." : "Type the show's name, then pick it from the results."}</div>`;
    return;
  }
  root.innerHTML = state.showResults.map((show, index) => `
    <div class="show-result ${state.selectedShow?.id === show.id && state.selectedShow?.source === show.source ? "selected" : ""}" data-index="${index}">
      ${show.imageUrl ? `<img src="${esc(show.imageUrl)}" alt="">` : `<div class="img-placeholder"></div>`}
      <div class="meta">
        <div class="name">${esc(show.name)}</div>
        <div class="sub">${esc([show.premiered?.slice(0, 4), show.network, show.source].filter(Boolean).join(" · "))}</div>
      </div>
    </div>`).join("");
  root.querySelectorAll(".show-result").forEach((el) => {
    el.onclick = () => selectShow(state.showResults[Number(el.dataset.index)]);
  });
}

function renderRenamePanel() {
  const panel = document.getElementById("rename-right");
  if (!panel) return;

  if (!state.selectedShow) {
    panel.innerHTML = `
      <div class="panel-title">2 · Review &amp; rename</div>
      <div class="panel-empty">Pick a show on the left to preview the new names.</div>`;
    return;
  }
  if (state.proposals === null) {
    panel.innerHTML = `
      <div class="panel-title">2 · Review &amp; rename</div>
      <div class="panel-empty"><span class="spin"></span> Loading episodes from TVMaze…</div>`;
    return;
  }

  const selectedCount = state.proposals.filter((p) => state.proposalChecks[p.itemId] && p.canApply).length;
  panel.innerHTML = `
    <div class="panel-title" style="display:flex;align-items:center;gap:10px">
      <span style="flex:1">2 · ${esc(state.selectedShow.name)}</span>
      <button class="btn small" id="apply-renames" ${selectedCount && !state.renameBusy ? "" : "disabled"}>
        ${state.renameBusy ? "Renaming…" : `Rename ${selectedCount} file${selectedCount === 1 ? "" : "s"}`}
      </button>
    </div>
    <table class="rename-table">
      <thead><tr><th class="col-check"></th><th>Current name</th><th class="arrow"></th><th>New name</th></tr></thead>
      <tbody>
        ${state.proposals.map((p) => `
          <tr class="${p.problem ? "problem" : ""}">
            <td class="col-check"><input type="checkbox" data-check="${esc(p.itemId)}"
              ${state.proposalChecks[p.itemId] && p.canApply ? "checked" : ""} ${p.canApply ? "" : "disabled"}></td>
            <td title="${esc(p.oldName)}">${esc(p.oldName)}</td>
            <td class="arrow">&#8594;</td>
            <td class="new-name" title="${esc(p.problem || p.newName)}">
              ${p.problem ? `<span class="rename-problem">${esc(p.problem)}</span>` : esc(p.newName)}
            </td>
          </tr>`).join("")}
      </tbody>
    </table>`;

  panel.querySelectorAll("[data-check]").forEach((box) => {
    box.onchange = () => {
      state.proposalChecks[box.dataset.check] = box.checked;
      const count = state.proposals.filter((p) => state.proposalChecks[p.itemId] && p.canApply).length;
      const btn = panel.querySelector("#apply-renames");
      btn.disabled = count === 0 || state.renameBusy;
      btn.textContent = `Rename ${count} file${count === 1 ? "" : "s"}`;
    };
  });

  panel.querySelector("#apply-renames").onclick = async () => {
    const chosen = state.proposals.filter((p) => state.proposalChecks[p.itemId] && p.canApply);
    if (!chosen.length) return;
    state.renameBusy = true;
    renderRenamePanel();
    try {
      const outcomes = await post("/api/rename/apply", { proposals: chosen });
      const ok = outcomes.filter((o) => o.renamed).length;
      const failed = outcomes.filter((o) => !o.renamed);
      const warned = outcomes.find((o) => o.renamed && o.error);
      toast(`Renamed ${ok} file${ok === 1 ? "" : "s"}${failed.length ? `, ${failed.length} failed` : ""}`,
        failed.length ? "err" : "ok", 6000);
      if (warned) toast(warned.error, "info", 8000);
      await loadLibrary();
      await selectShow(state.selectedShow); // refresh proposals with new names
    } catch (err) {
      toast(err.message, "err");
    } finally {
      state.renameBusy = false;
      render();
    }
  };
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
            <div class="settings-row"><label>API key</label><input class="input" id="os-key" value="${esc(s.openSubtitles.apiKey)}" placeholder="Required"></div>
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
            <div class="settings-row"><label>API key</label><input class="input" id="tmdb-key" value="${esc(s.tmdb.apiKey)}" placeholder="Required"></div>
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
