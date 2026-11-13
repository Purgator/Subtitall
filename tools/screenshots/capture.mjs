// Regenerates the README screenshots in docs/.
//
//   node tools/screenshots/capture.mjs
//
// It serves the real UI (src/Subtitall/wwwroot, untouched) next to a stand-in for the /api
// endpoints that answers with the fictional data of demo-data.mjs, then photographs each view in
// headless Chrome/Edge at the size of the app window. Nothing is read from %APPDATA%/Subtitall,
// no provider is contacted and the app itself is not started.
//
// Needs Node 22+ and Chrome or Edge (set CHROME_PATH if it is not in a standard location).
// No npm dependencies. Dev tool only: it is not part of the solution nor of the published app.

import { spawn } from "node:child_process";
import { createServer } from "node:http";
import { existsSync } from "node:fs";
import { mkdir, mkdtemp, readFile, rm, writeFile } from "node:fs/promises";
import { tmpdir } from "node:os";
import { dirname, extname, join, normalize, resolve } from "node:path";
import { fileURLToPath } from "node:url";
import * as demo from "./demo-data.mjs";

const repo = resolve(dirname(fileURLToPath(import.meta.url)), "../..");
const wwwroot = join(repo, "src/Subtitall/wwwroot");
const outDir = join(repo, "docs");

// The Electron window is 1280x840 with its title bar; the page itself gets about this much.
const WINDOW = { width: 1280, height: 800 };
// The smallest size the window can be given (same proportions): enough for the Settings page.
const COMPACT = { width: 960, height: 600 };

const shots = [
  {
    file: "files.png", size: WINDOW, scenario: "searching",
    ready: `document.querySelectorAll(".lang-chip.have").length > 8 && !!document.querySelector(".lang-chip.searching") && !!document.querySelector(".source-pill")`,
  },
  {
    file: "pick-subtitle.png", size: WINDOW,
    act: `document.querySelector('.lang-chip[data-item="${demo.pickerItemId}"][data-lang="en"]').click()`,
    ready: `document.querySelectorAll(".cand-table tbody tr").length > 3`,
  },
  {
    file: "rename.png", size: WINDOW, tab: "rename",
    ready: `document.querySelectorAll(".rename-table tbody tr").length > 5`,
  },
  {
    file: "shows.png", size: WINDOW, tab: "shows",
    ready: `!!document.querySelector("#show-details .episode-table") && document.querySelector("#show-details .poster").complete`,
  },
  {
    file: "settings.png", size: COMPACT, tab: "settings",
    // The page opens on the long language list; the providers below it say more about the app.
    act: `document.querySelector("#card-gestdown").closest(".settings-section").scrollIntoView()`,
    ready: `!!document.querySelector(".provider-status.ok")`,
  },
];

// ---- Stand-in backend ----------------------------------------------------------------------------

const mime = { ".html": "text/html; charset=utf-8", ".css": "text/css", ".js": "text/javascript", ".svg": "image/svg+xml", ".png": "image/png", ".ico": "image/x-icon" };
let scenario = "idle";
const unexpected = [];

async function appVersion() {
  const csproj = await readFile(join(repo, "src/Subtitall/Subtitall.csproj"), "utf8");
  return csproj.match(/<Version>([^<]+)<\/Version>/)?.[1] ?? demo.meta.version;
}

function startServer(version) {
  const json = (res, body) => { res.writeHead(200, { "Content-Type": "application/json" }); res.end(JSON.stringify(body)); };
  const routes = {
    "GET /api/meta": () => ({ ...demo.meta, version }),
    "GET /api/settings": () => demo.settings,
    "GET /api/library": () => demo.library,
    "GET /api/providers/status": () => demo.providerStatus,
    "GET /api/update/status": () => ({ ...demo.updateStatus, message: `You're on the latest version (v${version})` }),
    "GET /api/subtitles/status": () => ({ running: scenario === "searching" }),
    "GET /api/subtitles/candidates": () => demo.candidates(),
    "POST /api/rename/proposals": () => demo.proposals(),
    "GET /api/shows": () => demo.shows(),
  };

  const server = createServer(async (req, res) => {
    const url = new URL(req.url, "http://localhost");
    const key = `${req.method} ${url.pathname}`;
    try {
      if (key === "GET /api/events") {
        res.writeHead(200, { "Content-Type": "text/event-stream", "Cache-Control": "no-cache" });
        res.write("retry: 2000\n\n");
        if (scenario === "searching")
          for (const event of demo.searchEvents()) res.write(`data: ${JSON.stringify(event)}\n\n`);
        return; // kept open, like the real stream
      }
      if (routes[key]) return json(res, routes[key]());
      const show = url.pathname.match(/^\/api\/shows\/[^/]+\/(\d+)$/);
      if (req.method === "GET" && show && demo.showDetails(+show[1])) return json(res, demo.showDetails(+show[1]));
      if (url.pathname.startsWith("/api/")) {
        unexpected.push(key);
        res.writeHead(404, { "Content-Type": "application/json" });
        return res.end(JSON.stringify({ message: `Not part of the demo: ${key}` }));
      }

      const relative = url.pathname === "/" ? "index.html" : decodeURIComponent(url.pathname).replace(/^\/+/, "");
      const path = normalize(join(wwwroot, relative));
      if (!path.startsWith(wwwroot)) { res.writeHead(403); return res.end(); }
      const content = await readFile(path);
      res.writeHead(200, { "Content-Type": mime[extname(path)] ?? "application/octet-stream" });
      res.end(content);
    } catch {
      res.writeHead(404);
      res.end();
    }
  });
  return new Promise((done) => server.listen(0, "127.0.0.1", () => done(server)));
}

// ---- Headless browser (DevTools protocol, no dependency) -----------------------------------------------

function findBrowser() {
  const candidates = [
    process.env.CHROME_PATH,
    "C:/Program Files/Google/Chrome/Application/chrome.exe",
    "C:/Program Files (x86)/Google/Chrome/Application/chrome.exe",
    "C:/Program Files (x86)/Microsoft/Edge/Application/msedge.exe",
    "C:/Program Files/Microsoft/Edge/Application/msedge.exe",
    "/Applications/Google Chrome.app/Contents/MacOS/Google Chrome",
    "/usr/bin/google-chrome", "/usr/bin/chromium", "/usr/bin/chromium-browser",
  ].filter(Boolean);
  const found = candidates.find((p) => existsSync(p));
  if (!found) throw new Error("Chrome or Edge not found — set CHROME_PATH to the browser executable.");
  return found;
}

const sleep = (ms) => new Promise((done) => setTimeout(done, ms));

async function launchBrowser(profileDir) {
  const child = spawn(findBrowser(), [
    "--headless=new", "--remote-debugging-port=0", `--user-data-dir=${profileDir}`,
    "--no-first-run", "--no-default-browser-check", "--disable-extensions", "--hide-scrollbars",
    "--force-device-scale-factor=1", "--lang=en-US", "about:blank",
  ], { stdio: "ignore" });
  const portFile = join(profileDir, "DevToolsActivePort");
  // The browser writes its debugging port there once it is up (the file may be locked for a moment).
  for (let i = 0; i < 100; i++) {
    await sleep(100);
    const port = (await readFile(portFile, "utf8").catch(() => "")).split("\n")[0].trim();
    if (port) return { child, port };
  }
  child.kill();
  throw new Error("The browser did not start.");
}

async function openPage(port) {
  const target = await (await fetch(`http://127.0.0.1:${port}/json/new?about:blank`, { method: "PUT" })).json();
  const socket = new WebSocket(target.webSocketDebuggerUrl);
  await new Promise((done, fail) => { socket.onopen = done; socket.onerror = () => fail(new Error("DevTools connection failed")); });
  let nextId = 0;
  const pending = new Map();
  const errors = [];
  socket.onmessage = ({ data }) => {
    const message = JSON.parse(data);
    if (message.method === "Runtime.exceptionThrown")
      errors.push(message.params.exceptionDetails.exception?.description ?? message.params.exceptionDetails.text);
    const waiter = pending.get(message.id);
    if (!waiter) return;
    pending.delete(message.id);
    if (message.error) waiter.fail(new Error(message.error.message));
    else waiter.done(message.result);
  };
  const send = (method, params = {}) => new Promise((done, fail) => {
    const id = ++nextId;
    pending.set(id, { done, fail });
    socket.send(JSON.stringify({ id, method, params }));
  });
  const evaluate = async (expression) => {
    const { result, exceptionDetails } = await send("Runtime.evaluate", { expression, returnByValue: true });
    if (exceptionDetails) throw new Error(exceptionDetails.exception?.description ?? exceptionDetails.text);
    return result.value;
  };
  const waitFor = async (expression, what) => {
    for (let i = 0; i < 100; i++) {
      if (await evaluate(expression).catch(() => false)) return;
      await sleep(100);
    }
    throw new Error(`Timed out waiting for ${what}`);
  };
  await send("Page.enable");
  await send("Runtime.enable");
  return { send, evaluate, waitFor, errors, close: () => socket.close() };
}

// ---- Main -----------------------------------------------------------------------------------------

const version = await appVersion();
const server = await startServer(version);
const origin = `http://127.0.0.1:${server.address().port}`;
const profileDir = await mkdtemp(join(tmpdir(), "subtitall-shots-"));
let browser;

try {
  browser = await launchBrowser(profileDir);
  const page = await openPage(browser.port);
  await page.send("Emulation.setLocaleOverride", { locale: "en-US" }).catch(() => {});
  await mkdir(outDir, { recursive: true });

  for (const shot of shots) {
    scenario = shot.scenario ?? "idle";
    await page.send("Emulation.setDeviceMetricsOverride", { ...shot.size, deviceScaleFactor: 1, mobile: false });
    await page.send("Page.navigate", { url: `${origin}/` });
    await page.waitFor(`document.querySelectorAll(".file-table tbody tr").length === ${demo.library.length}`, "the file list");
    if (shot.tab) await page.evaluate(`document.querySelector('.tab[data-view="${shot.tab}"]').click()`);
    await page.waitFor(shot.act ? "true" : shot.ready, shot.file);
    if (shot.act) await page.evaluate(shot.act);
    await page.waitFor(shot.ready, shot.file);
    await page.evaluate("document.fonts.ready.then(() => true)");
    await sleep(400); // let transitions settle

    const { data } = await page.send("Page.captureScreenshot", { format: "png" });
    const png = Buffer.from(data, "base64");
    await writeFile(join(outDir, shot.file), png);
    console.log(`docs/${shot.file}  ${shot.size.width}x${shot.size.height}  ${Math.round(png.length / 1024)} KB`);
  }

  await page.send("Browser.close").catch(() => {});
  page.close();
  if (page.errors.length) throw new Error(`The UI threw: ${page.errors.join(" | ")}`);
  if (unexpected.length) throw new Error(`The UI called endpoints the demo does not cover: ${[...new Set(unexpected)].join(", ")}`);
} finally {
  browser?.child.kill();
  server.closeAllConnections();
  server.close();
  await sleep(500); // the browser releases its profile files a moment after exiting
  await rm(profileDir, { recursive: true, force: true, maxRetries: 5, retryDelay: 300 }).catch(() => {});
}
