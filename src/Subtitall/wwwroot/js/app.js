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

// ---------- Data loading -----------------------------------------------------------

async function loadLibrary() {
  state.items = await get("/api/library");
  // Don't nuke the Settings form (and any unsaved input) over a library change —
  // that view doesn't display library data anyway.
  if (state.view === "settings") renderStatusBar();
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
        renderStatusBar();
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
        renderStatusBar();
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
  document.querySelectorAll(".activity-btn").forEach((btn) =>
    btn.classList.toggle("active", btn.dataset.view === state.view));
  renderSidebar();
  renderMain();
  renderStatusBar();
}

function renderStatusBar() {
  document.getElementById("status-version").textContent = `Subtitall v${state.meta.version}`;
  const s = state.settings;
  if (s) {
    const active = [
      s.gestdown?.enabled ? "Gestdown" : null,
      s.openSubtitles?.enabled && s.openSubtitles?.apiKey ? "OpenSubtitles" : null,
      s.subDl?.enabled && s.subDl?.apiKey ? "SubDL" : null,
    ].filter(Boolean);
    document.getElementById("status-providers").textContent =
      active.length ? `Providers: ${active.join(", ")}` : "No provider configured";
  }
  const progress = document.getElementById("status-progress");
  progress.textContent = state.searching
    ? `Searching subtitles… ${state.progress.done}/${state.progress.total}`
    : "Ready";
}

// ---------- Rendering: files view --------------------------------------------------------------

function renderSidebar() {
  const sidebar = document.getElementById("sidebar");
  if (state.view === "files") {
    sidebar.innerHTML = `
      <div class="sidebar-title">Files</div>
      <div class="sidebar-section">
        <button class="btn" id="add-files">Add video files…</button>
        <button class="btn secondary" id="add-folder">Add folder…</button>
        <button class="btn secondary danger" id="clear-list" ${state.items.length ? "" : "disabled"}>Clear list</button>
      </div>
      <div class="sidebar-hint">
        Drop videos or folders anywhere in this window, then hit
        <b>Search subtitles</b>.<br><br>
        Languages: <b>${esc((state.settings?.languages || []).join(", "))}</b>
      </div>`;
    sidebar.querySelector("#add-files").onclick = () => browse("files");
    sidebar.querySelector("#add-folder").onclick = () => browse("folder");
    sidebar.querySelector("#clear-list").onclick = async () => {
      await del("/api/library");
    };
  } else if (state.view === "rename") {
    sidebar.innerHTML = `
      <div class="sidebar-title">Rename episodes</div>
      <div class="sidebar-section">
        <input class="input" id="show-search" placeholder="Search show on TVMaze…" value="${esc(state.showQuery)}">
      </div>
      <div id="show-results"></div>
      <div class="sidebar-hint" id="show-hint"></div>`;
    const input = sidebar.querySelector("#show-search");
    input.oninput = debounce(async () => {
      state.showQuery = input.value.trim();
      if (state.showQuery.length < 2) { state.showResults = []; renderShowResults(); return; }
      try {
        state.showResults = await get(`/api/rename/shows?q=${encodeURIComponent(state.showQuery)}`);
      } catch { state.showResults = []; }
      renderShowResults();
    }, 400);
    renderShowResults();
  } else {
    sidebar.innerHTML = `
      <div class="sidebar-title">Settings</div>
      <div class="sidebar-hint">
        Configure subtitle languages and providers.<br><br>
        <b>Gestdown</b> works without any key (TV shows).<br><br>
        <b>OpenSubtitles</b> and <b>SubDL</b> need a free API key for
        more sources and movie coverage.
      </div>`;
  }
}

function renderShowResults() {
  const root = document.getElementById("show-results");
  if (!root) return;
  root.innerHTML = state.showResults.map((show) => `
    <div class="show-result ${state.selectedShow?.id === show.id ? "selected" : ""}" data-id="${show.id}">
      ${show.imageUrl ? `<img src="${esc(show.imageUrl)}" alt="">` : `<div style="width:32px;height:44px;background:var(--bg-input);border-radius:2px;flex:none"></div>`}
      <div class="meta">
        <div class="name">${esc(show.name)}</div>
        <div class="sub">${esc([show.premiered?.slice(0, 4), show.network].filter(Boolean).join(" · "))}</div>
      </div>
    </div>`).join("");
  root.querySelectorAll(".show-result").forEach((el) => {
    el.onclick = () => selectShow(state.showResults.find((s) => String(s.id) === el.dataset.id));
  });
  const hint = document.getElementById("show-hint");
  if (hint && !state.showResults.length) {
    hint.innerHTML = state.showQuery
      ? "No results."
      : "Search for the TV show your files belong to, then review the proposed names.";
  } else if (hint) hint.innerHTML = "";
}

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

function renderMain() {
  const main = document.getElementById("main");
  if (state.view === "files") renderFilesView(main);
  else if (state.view === "rename") renderRenameView(main);
  else renderSettingsView(main);
}

function renderFilesView(main) {
  if (!state.items.length) {
    main.innerHTML = `
      <div class="empty-state">
        <svg viewBox="0 0 24 24"><path d="M4 4h6l2 2h8a1 1 0 0 1 1 1v12a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V5a1 1 0 0 1 1-1z" fill="none" stroke="currentColor" stroke-width="1"/></svg>
        <div>No videos yet — drop files or folders here, or use the buttons.</div>
        <div class="actions">
          <button class="btn" id="empty-add-files">Add video files…</button>
          <button class="btn secondary" id="empty-add-folder">Add folder…</button>
        </div>
      </div>`;
    main.querySelector("#empty-add-files").onclick = () => browse("files");
    main.querySelector("#empty-add-folder").onclick = () => browse("folder");
    return;
  }

  main.innerHTML = `
    <div class="toolbar">
      ${state.searching
        ? `<button class="btn danger" id="cancel-search">Cancel</button><span class="spin"></span>`
        : `<button class="btn" id="start-search">Search subtitles</button>`}
      <span class="toolbar-info">${state.items.length} video${state.items.length > 1 ? "s" : ""}</span>
      <div class="grow"></div>
      <span class="toolbar-info">Click a language chip to pick a subtitle manually</span>
    </div>
    <table class="file-table">
      <thead><tr>
        <th class="col-langs">Subtitles</th>
        <th>File</th>
        <th class="col-parsed">Detected</th>
        <th class="col-size">Size</th>
        <th class="col-actions"></th>
      </tr></thead>
      <tbody>${state.items.map(renderFileRowHtml).join("")}</tbody>
    </table>`;

  const startBtn = main.querySelector("#start-search");
  if (startBtn) startBtn.onclick = startSearch;
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

async function startSearch() {
  try {
    await post("/api/subtitles/search", {});
  } catch (err) {
    toast(err.message, "err");
  }
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
            <td class="release" title="${esc(c.releaseName)}">${esc(c.releaseName) || "<i>unnamed</i>"}</td>
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
    const proposals = await post("/api/rename/proposals", { showId: show.id, showName: show.name });
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
    main.innerHTML = `<div class="empty-state"><div>Add some episodes in the Files view first.</div></div>`;
    return;
  }
  if (!state.selectedShow) {
    main.innerHTML = `
      <div class="empty-state">
        <svg viewBox="0 0 24 24"><path d="M16.5 4.5l3 3L8 19H5v-3L16.5 4.5z" fill="none" stroke="currentColor" stroke-width="1"/></svg>
        <div>Search and select the show in the sidebar to preview new names.</div>
      </div>`;
    return;
  }
  if (state.proposals === null) {
    main.innerHTML = `<div class="empty-state"><span class="spin"></span><div>Loading episodes from TVMaze…</div></div>`;
    return;
  }

  const selectedCount = state.proposals.filter((p) => state.proposalChecks[p.itemId] && p.canApply).length;
  main.innerHTML = `
    <div class="toolbar">
      <button class="btn" id="apply-renames" ${selectedCount && !state.renameBusy ? "" : "disabled"}>
        ${state.renameBusy ? "Renaming…" : `Rename ${selectedCount} file${selectedCount === 1 ? "" : "s"}`}
      </button>
      <span class="toolbar-info">${esc(state.selectedShow.name)} — template: <code>${esc(state.settings?.renameTemplate || "")}</code></span>
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

  main.querySelectorAll("[data-check]").forEach((box) => {
    box.onchange = () => {
      state.proposalChecks[box.dataset.check] = box.checked;
      const count = state.proposals.filter((p) => state.proposalChecks[p.itemId] && p.canApply).length;
      const btn = main.querySelector("#apply-renames");
      btn.disabled = count === 0 || state.renameBusy;
      btn.textContent = `Rename ${count} file${count === 1 ? "" : "s"}`;
    };
  });

  main.querySelector("#apply-renames").onclick = async () => {
    const chosen = state.proposals.filter((p) => state.proposalChecks[p.itemId] && p.canApply);
    if (!chosen.length) return;
    state.renameBusy = true;
    render();
    try {
      const outcomes = await post("/api/rename/apply", { proposals: chosen });
      const ok = outcomes.filter((o) => o.renamed).length;
      const failed = outcomes.filter((o) => !o.renamed);
      toast(`Renamed ${ok} file${ok === 1 ? "" : "s"}${failed.length ? `, ${failed.length} failed` : ""}`,
        failed.length ? "err" : "ok", 6000);
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
    <div class="settings-page">
      <h2>Settings</h2>

      <div class="settings-section">
        <h3>Subtitle languages</h3>
        <div class="desc">Subtitles are fetched for every selected language and saved as <code>video.&lt;lang&gt;.srt</code>.</div>
        <div class="lang-grid">
          ${state.meta.languages.map((l) => `
            <label class="lang-option">
              <input type="checkbox" data-lang="${l.code}" ${selected.has(l.code) ? "checked" : ""}>
              <span>${esc(l.name)} <span style="color:var(--fg-dim)">(${l.code})</span></span>
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
            <span style="color:var(--fg-dim);font-size:12px">TV shows (Addic7ed mirror)</span>
          </div>
        </div>

        <div class="provider-card ${s.openSubtitles.enabled ? "enabled" : ""}" id="card-opensubtitles">
          <div class="head">
            <label class="toggle"><input type="checkbox" id="os-enabled" ${s.openSubtitles.enabled ? "checked" : ""}><span class="slider"></span></label>
            <span class="name">OpenSubtitles</span>
            <span style="color:var(--fg-dim);font-size:12px">movies + TV — <a href="https://www.opensubtitles.com/en/consumers" target="_blank">get a free API key</a></span>
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
            <span style="color:var(--fg-dim);font-size:12px">movies + TV — <a href="https://subdl.com/panel/api" target="_blank">get a free API key</a></span>
          </div>
          <div class="fields">
            <div class="settings-row"><label>API key</label><input class="input" id="subdl-key" value="${esc(s.subDl.apiKey)}" placeholder="Required"></div>
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
        <div class="settings-row"><label>Template</label><input class="input" id="rename-template" value="${esc(s.renameTemplate)}" style="max-width:420px"></div>
      </div>

      <button class="btn" id="save-settings">Save settings</button>
    </div>`;

  ["gestdown", "opensubtitles", "subdl"].forEach((key) => {
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
      overwriteExisting: main.querySelector("#overwrite").checked,
      renameTemplate: main.querySelector("#rename-template").value.trim() || "{Show} - S{Season}E{Episode} - {Title}",
    };
    try {
      state.settings = await put("/api/settings", updated);
      toast("Settings saved", "ok");
      renderStatusBar();
    } catch (err) {
      toast(`Could not save: ${err.message}`, "err");
    }
  };
}

// ---------- Init ------------------------------------------------------------------------------------

function setupNavigation() {
  document.querySelectorAll(".activity-btn").forEach((btn) => {
    btn.onclick = () => { state.view = btn.dataset.view; render(); };
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
