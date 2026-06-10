"use strict";

/* ------------------------------------------------------------------ helpers */

const $ = (sel) => document.querySelector(sel);
const $$ = (sel) => [...document.querySelectorAll(sel)];

const api = {
  get: (url) => fetch(url).then(r => r.ok ? r.json() : r.json().catch(() => ({})).then(e => Promise.reject(e))),
  post: (url, body) => fetch(url, {
    method: "POST",
    headers: body ? { "Content-Type": "application/json" } : {},
    body: body ? JSON.stringify(body) : undefined,
  }).then(r => r.ok ? r.json().catch(() => ({})) : r.json().catch(() => ({})).then(e => Promise.reject(e))),
  del: (url) => fetch(url, { method: "DELETE" }),
  putText: (url, text) => fetch(url, { method: "PUT", headers: { "Content-Type": "text/plain" }, body: text }).then(r => r.json()),
};

const esc = (s) => String(s ?? "").replace(/[&<>"']/g, c => ({ "&": "&amp;", "<": "&lt;", ">": "&gt;", '"': "&quot;", "'": "&#39;" }[c]));
const fmtTime = (iso) => iso ? new Date(iso).toLocaleTimeString() : "";
const fmtBytes = (n) => n > 1048576 ? (n / 1048576).toFixed(1) + " MB" : (n / 1024).toFixed(0) + " KB";

function fmtDur(s) {
  if (s == null) return "–";
  if (s < 60) return s.toFixed(1) + "s";
  if (s < 3600) return `${Math.floor(s / 60)}m ${Math.floor(s % 60)}s`;
  return `${Math.floor(s / 3600)}h ${Math.floor(s % 3600 / 60)}m`;
}

const audioUrl = (name) => "/api/audio/" + encodeURIComponent(name);
const estWords = (t) => t.trim() ? t.trim().split(/\s+/).length : 0;
const estTokens = (t) => Math.max(150, Math.round(estWords(t) * 65));
const estSeconds = (t) => estWords(t) / 2.5;

/* ------------------------------------------------------------------ toasts */

function toast(msg, { kind = "ok", playUrl = null, ms = 6000 } = {}) {
  const el = document.createElement("div");
  el.className = "toast" + (kind === "err" ? " err" : "");
  el.innerHTML = `<span class="t-msg">${esc(msg)}</span>`;
  if (playUrl) {
    const btn = document.createElement("button");
    btn.className = "small";
    btn.textContent = "▶";
    btn.onclick = () => new Audio(playUrl).play();
    el.appendChild(btn);
  }
  const x = document.createElement("button");
  x.className = "small";
  x.textContent = "✕";
  x.onclick = () => el.remove();
  el.appendChild(x);
  $("#toasts").appendChild(el);
  setTimeout(() => el.remove(), ms);
}

/* ------------------------------------------------------------------ theme */

function applyTheme(t) {
  document.documentElement.dataset.theme = t;
  localStorage.setItem("theme", t);
  setTimeout(redrawAllWaves, 50);
}
applyTheme(localStorage.getItem("theme") || "dark");
$("#theme-toggle").onclick = () =>
  applyTheme(document.documentElement.dataset.theme === "light" ? "dark" : "light");

/* ------------------------------------------------------------------ tabs */

let activeTab = "dashboard";
$$("nav button").forEach(btn => {
  btn.addEventListener("click", () => {
    activeTab = btn.dataset.tab;
    $$("nav button").forEach(b => b.classList.toggle("active", b === btn));
    $$(".tab").forEach(t => t.classList.toggle("active", t.id === "tab-" + activeTab));
    refreshTab(true);
  });
});

function switchTab(name) {
  const btn = $$("nav button").find(b => b.dataset.tab === name);
  if (btn) btn.click();
}

/* ------------------------------------------------------------------ SSE */

const jobsCache = new Map();   // id -> job
const jobEstimates = new Map(); // id -> estimated tokens
let sseOk = false;

function connectSse() {
  const es = new EventSource("/api/events");
  es.onopen = () => { sseOk = true; $("#sse-dot").classList.add("on"); };
  es.onerror = () => { sseOk = false; $("#sse-dot").classList.remove("on"); };
  es.addEventListener("job", (e) => onJobEvent(JSON.parse(e.data)));
  es.addEventListener("log", (e) => { appendLog(JSON.parse(e.data)); renderLogsSoon(); });
}

function onJobEvent(job) {
  const prev = jobsCache.get(job.id);
  jobsCache.set(job.id, job);

  updateLiveCard(job);
  if (currentGroup && job.group === currentGroup.id) updateGroupItem(job);

  const finished = (!prev || prev.status === "queued" || prev.status === "running") &&
    (job.status === "done" || job.status === "error");
  if (finished) {
    const inGroup = currentGroup && job.group === currentGroup.id;
    if (job.status === "error")
      toast(`✘ ${job.description}: ${job.error || "failed"}`, { kind: "err" });
    else if (!inGroup && job.outputFile)
      toast(`✔ ${job.outputFile} (${(job.seconds ?? 0).toFixed(1)}s)`, { playUrl: audioUrl(job.outputFile) });
    refreshStatusSoon();
    if (activeTab === "dashboard") refreshOutputs();
    if (activeTab === "library") refreshLibrary();
  }
  renderJobsSoon();
}

/* ------------------------------------------------------------------ status */

let lastStatus = null;
let statusTimer = null;

function refreshStatusSoon() { clearTimeout(statusTimer); statusTimer = setTimeout(refreshStatus, 300); }

async function refreshStatus() {
  try { lastStatus = await api.get("/api/status"); } catch { return; }
  const s = lastStatus;

  const badge = $("#engine-badge");
  if (!s.modelsReady) { badge.textContent = "models missing"; badge.className = "badge warn"; }
  else if (s.engineState === "loaded") { badge.textContent = "engine ready"; badge.className = "badge ok"; }
  else if (s.engineState === "loading") { badge.textContent = "loading model…"; badge.className = "badge warn"; }
  else if (s.engineState === "failed") { badge.textContent = "engine failed"; badge.className = "badge err"; }
  else { badge.textContent = "engine idle"; badge.className = "badge"; }

  $("#quant-label").textContent = s.quant;
  $("#gpu-label").textContent = s.gpuLayers;
  $("#root-dir").textContent = s.rootDir;

  $("#st-engine").innerHTML =
    s.engineState === "loaded" ? `<span style="color:var(--ok)">loaded ✔</span>` :
    s.engineState === "loading" ? `<span style="color:var(--accent)">loading…</span>` :
    s.engineState === "failed" ? `<span style="color:var(--err)">failed</span>` : "idle";
  $("#btn-engine-load").classList.toggle("hidden", !(s.modelsReady && s.engineState === "unloaded"));

  $("#st-models").innerHTML = s.modelsReady
    ? `<span style="color:var(--ok)">ready ✔</span>`
    : `<span style="color:var(--accent)">missing</span>`;
  $("#btn-download").classList.toggle("hidden", s.modelsReady);

  const dl = Object.values(s.downloads || {});
  $("#download-progress").innerHTML = dl
    .filter(d => d.status === "downloading" || d.status === "error")
    .map(d => `
      <div class="dl-row">${esc(d.name)} — ${d.status === "error" ? `<span style="color:var(--err)">${esc(d.error)}</span>`
        : `${(d.bytesReceived / 1048576).toFixed(0)} / ${(d.totalBytes / 1048576).toFixed(0)} MB`}
        <div class="progressbar"><div style="width:${d.percent}%"></div></div>
      </div>`).join("");

  syncSpeakerSelects(s.speakers || ["default"]);
}

function syncSpeakerSelects(names) {
  for (const sel of [$("#gen-speaker"), ...$$("#dlg-roles select")]) {
    if (!sel) continue;
    const current = sel.value;
    if ([...sel.options].map(o => o.value).join("|") !== names.join("|")) {
      sel.innerHTML = names.map(n => `<option value="${esc(n)}">${esc(n)}</option>`).join("");
      if (names.includes(current)) sel.value = current;
    }
  }
}

$("#btn-download").onclick = async () => { await api.post("/api/models/download"); refreshStatus(); };
$("#btn-engine-load").onclick = async () => { await api.post("/api/engine/load"); refreshStatus(); };

/* ------------------------------------------------------------------ dashboard stats */

async function refreshStats() {
  let st;
  try { st = await api.get("/api/stats"); } catch { return; }
  $("#st-audio").textContent = fmtDur(st.totalAudioSeconds);
  $("#st-jobs-done").textContent = `${st.totalJobsDone} jobs done`;
  $("#st-speed").textContent = st.tokensPerSec ? `${st.tokensPerSec} tok/s` : "–";
  $("#st-rt").textContent = st.realtimeFactor ? `${st.realtimeFactor}× realtime` : "";
  $("#st-mem").textContent = `${st.memoryMb} MB`;
  $("#st-uptime").textContent = `up ${fmtDur(st.uptimeSeconds)}`;
  $("#model-files").innerHTML = (st.modelFiles || []).map(f => `<div>${esc(f.name)} <span class="hint">${f.mb} MB</span></div>`).join("") || "–";
  drawChart(st.chart || []);
}

function drawChart(data) {
  const canvas = $("#chart");
  const dpr = window.devicePixelRatio || 1;
  const w = canvas.clientWidth || 400, h = canvas.getAttribute("height") | 0;
  canvas.width = w * dpr; canvas.height = h * dpr;
  const g = canvas.getContext("2d");
  g.clearRect(0, 0, canvas.width, canvas.height);
  if (data.length === 0) {
    g.fillStyle = cssVar("--muted"); g.font = `${12 * dpr}px sans-serif`;
    g.fillText("no generations yet", 10 * dpr, h * dpr / 2);
    return;
  }
  const max = Math.max(...data.map(d => d.seconds), 1);
  const barW = canvas.width / Math.max(data.length, 12);
  g.font = `${10 * dpr}px sans-serif`;
  data.forEach((d, i) => {
    const bh = (d.seconds / max) * (canvas.height - 18 * dpr);
    g.fillStyle = cssVar("--accent2");
    g.fillRect(i * barW + barW * 0.15, canvas.height - bh - 14 * dpr, barW * 0.7, bh);
    g.fillStyle = cssVar("--muted");
    if (data.length <= 16 || i % 2 === 0)
      g.fillText(d.seconds.toFixed(0), i * barW + barW * 0.2, canvas.height - 3 * dpr);
  });
}

async function refreshOutputs() {
  let files;
  try { files = await api.get("/api/outputs"); } catch { return; }
  const box = $("#recent-outputs");
  box.innerHTML = "";
  if (files.length === 0) { box.innerHTML = `<p class="hint">Nothing generated yet — try the Studio.</p>`; return; }
  for (const f of files.slice(0, 8)) {
    box.appendChild(new WavePlayer(audioUrl(f.name), {
      title: f.name,
      subtitle: `${f.seconds != null ? f.seconds.toFixed(1) + "s · " : ""}${fmtBytes(f.size)}`,
    }).el);
  }
}

/* ------------------------------------------------------------------ studio */

let studioMode = "single";
let takes = 1;
let currentGroup = null; // {id, kind, items:[{jobId,label,status,outputFile,seconds,error,el}], stitch, gap}

$("#mode-seg").addEventListener("click", (e) => {
  if (e.target.tagName !== "BUTTON") return;
  studioMode = e.target.dataset.mode;
  $$("#mode-seg button").forEach(b => b.classList.toggle("active", b === e.target));
  $("#single-panel").classList.toggle("hidden", studioMode !== "single");
  $("#dialogue-panel").classList.toggle("hidden", studioMode !== "dialogue");
});

$("#takes-seg").addEventListener("click", (e) => {
  if (e.target.tagName !== "BUTTON") return;
  takes = parseInt(e.target.textContent);
  $$("#takes-seg button").forEach(b => b.classList.toggle("active", b === e.target));
});

// sliders -> live value bubbles
$$(".slider input[type=range]").forEach(input => {
  const out = input.parentElement.querySelector("output");
  input.addEventListener("input", () => out.textContent = input.value);
});
$("#dlg-gap").addEventListener("input", () => $("#dlg-gap-val").textContent = $("#dlg-gap").value + "s");
$("#lib-gap").addEventListener("input", () => $("#lib-gap-val").textContent = $("#lib-gap").value + "s");

$("#seed-dice").onclick = (e) => { e.preventDefault(); $("#s-seed").value = Math.floor(Math.random() * 2 ** 31); };
$("#sampler-reset").onclick = (e) => {
  e.preventDefault();
  const defaults = { "s-temp": 0.4, "s-rep": 1.1, "s-topp": 0.9, "s-minp": 0.05, "s-topk": 40, "s-seed": 0 };
  for (const [id, v] of Object.entries(defaults)) {
    const el = document.getElementById(id);
    el.value = v;
    const out = el.parentElement.querySelector("output");
    if (out) out.textContent = v;
  }
};

function samplerSettings() {
  return {
    temperature: parseFloat($("#s-temp").value),
    repetitionPenalty: parseFloat($("#s-rep").value),
    topP: parseFloat($("#s-topp").value),
    minP: parseFloat($("#s-minp").value),
    topK: parseInt($("#s-topk").value),
  };
}

const textStats = () => {
  const t = $("#gen-text").value;
  $("#text-stats").textContent = t.trim()
    ? `${estWords(t)} words · ${t.length} chars · ~${estSeconds(t).toFixed(0)}s of audio expected`
    : "";
};
$("#gen-text").addEventListener("input", textStats);
textStats();

// ---- dialogue role parsing

let roleMap = {};
function parseDialogue() {
  const lines = $("#dlg-text").value.split(/\r?\n/).map(l => l.trim()).filter(Boolean);
  const cues = lines.map(l => {
    const m = l.match(/^([^:]{1,40}):\s*(.+)$/);
    return m ? { role: m[1].trim(), text: m[2].trim() } : { role: "Narrator", text: l };
  });
  const roles = [...new Set(cues.map(c => c.role))];
  const box = $("#dlg-roles");
  const speakers = lastStatus?.speakers || ["default"];
  box.innerHTML = roles.map(r => `
    <div class="role-row">
      <span class="rname" title="${esc(r)}">${esc(r)}</span>
      <select data-role="${esc(r)}">${speakers.map(s =>
        `<option value="${esc(s)}" ${roleMap[r] === s ? "selected" : ""}>${esc(s)}</option>`).join("")}</select>
    </div>`).join("");
  box.querySelectorAll("select").forEach(sel =>
    sel.addEventListener("change", () => roleMap[sel.dataset.role] = sel.value));
  return cues;
}
let dlgTimer = null;
$("#dlg-text").addEventListener("input", () => { clearTimeout(dlgTimer); dlgTimer = setTimeout(parseDialogue, 400); });

// ---- generate

$("#btn-generate").onclick = async () => {
  if (studioMode === "single") await submitSingle();
  else await submitDialogue();
};

async function submitSingle() {
  const text = $("#gen-text").value.trim();
  if (!text) return;
  const groupId = "g" + Date.now();
  const baseSeed = parseInt($("#s-seed").value) || 0;
  const settings = samplerSettings();

  currentGroup = { id: groupId, kind: "takes", items: [], stitch: false };
  $("#results").innerHTML = "";

  for (let i = 0; i < takes; i++) {
    const label = takes > 1 ? `take ${i + 1}` : "result";
    const seed = baseSeed > 0 ? baseSeed + i : Math.floor(Math.random() * 2 ** 31);
    try {
      const r = await api.post("/api/generate", {
        text, speaker: $("#gen-speaker").value, ...settings, seed,
        group: groupId, label,
      });
      jobEstimates.set(r.jobId, estTokens(text));
      addGroupItem(r.jobId, label, `seed ${seed}`);
    } catch (e) {
      toast(e.error || "generate failed", { kind: "err" });
      return;
    }
  }
}

async function submitDialogue() {
  const cues = parseDialogue();
  if (cues.length === 0) return;
  const groupId = "g" + Date.now();
  const settings = samplerSettings();
  currentGroup = {
    id: groupId, kind: "dialogue", items: [],
    stitch: $("#dlg-concat").checked,
    gap: parseFloat($("#dlg-gap").value),
  };
  $("#results").innerHTML = "";

  for (let i = 0; i < cues.length; i++) {
    const cue = cues[i];
    const label = `${i + 1}. ${cue.role}`;
    try {
      const r = await api.post("/api/generate", {
        text: cue.text, speaker: roleMap[cue.role] || "default", ...settings,
        seed: Math.floor(Math.random() * 2 ** 31),
        group: groupId, label,
      });
      jobEstimates.set(r.jobId, estTokens(cue.text));
      addGroupItem(r.jobId, label, `“${cue.text.slice(0, 60)}${cue.text.length > 60 ? "…" : ""}”`);
    } catch (e) {
      toast(e.error || "generate failed", { kind: "err" });
      return;
    }
  }
}

function addGroupItem(jobId, label, sub) {
  const el = document.createElement("div");
  el.className = "result-item";
  el.innerHTML = `
    <div class="ri-head"><span class="lbl">${esc(label)}</span><span>${esc(sub)}</span>
      <span class="spacer"></span><span class="ri-status">queued…</span>
      <button class="small danger ri-cancel">✕</button></div>
    <div class="ri-body"></div>`;
  el.querySelector(".ri-cancel").onclick = () => api.post(`/api/jobs/${jobId}/cancel`).catch(() => {});
  $("#results").appendChild(el);
  currentGroup.items.push({ jobId, label, status: "queued", el });
}

function updateGroupItem(job) {
  const item = currentGroup.items.find(i => i.jobId === job.id);
  if (!item) return;
  item.status = job.status;
  item.outputFile = job.outputFile;
  const statusEl = item.el.querySelector(".ri-status");
  const body = item.el.querySelector(".ri-body");
  const cancel = item.el.querySelector(".ri-cancel");

  if (job.status === "running") {
    statusEl.textContent = job.detail || "running…";
  } else if (job.status === "done" && job.outputFile && !item.rendered) {
    item.rendered = true;
    statusEl.textContent = `${(job.seconds ?? 0).toFixed(1)}s`;
    cancel.remove();
    body.appendChild(new WavePlayer(audioUrl(job.outputFile), { compact: true }).el);
    maybeStitchGroup();
  } else if (job.status === "error" || job.status === "cancelled") {
    statusEl.textContent = job.status;
    body.innerHTML = `<div class="err">${esc(job.error || job.status)}</div>`;
    cancel.remove();
  }
}

async function maybeStitchGroup() {
  const g = currentGroup;
  if (!g || !g.stitch || g.stitched) return;
  if (!g.items.every(i => i.status === "done" && i.outputFile)) return;
  g.stitched = true;
  try {
    const r = await api.post("/api/outputs/concat", {
      files: g.items.map(i => i.outputFile),
      gapSeconds: g.gap,
      name: `dialogue-${new Date().toISOString().slice(11, 19).replaceAll(":", "")}`,
    });
    const el = document.createElement("div");
    el.className = "result-item";
    el.innerHTML = `<div class="ri-head"><span class="lbl">🎬 stitched dialogue</span>
      <span>${r.seconds.toFixed(1)}s</span></div><div class="ri-body"></div>`;
    el.querySelector(".ri-body").appendChild(new WavePlayer(audioUrl(r.name), { title: r.name }).el);
    $("#results").prepend(el);
  } catch (e) {
    toast("stitch failed: " + (e.error || ""), { kind: "err" });
  }
}

function updateLiveCard(job) {
  if (job.type !== "generate" && job.type !== "plugin" && job.type !== "clone") return;
  if (job.status === "running") {
    $("#live-status").textContent = `${job.description}`;
    $("#live-meta").textContent = job.detail || "";
    const est = jobEstimates.get(job.id);
    if (est && job.tokens) {
      $("#live-bar").style.width = Math.min(96, job.tokens / est * 100) + "%";
    } else if (job.tokens) {
      $("#live-bar").style.width = Math.min(96, job.tokens / 1500 * 100) + "%";
    }
  } else if (job.status === "done" || job.status === "error" || job.status === "cancelled") {
    const anyRunning = [...jobsCache.values()].some(j => j.status === "running");
    if (!anyRunning) {
      $("#live-status").textContent = "idle — nothing running";
      $("#live-meta").textContent = "";
      $("#live-bar").style.width = job.status === "done" ? "100%" : "0%";
      setTimeout(() => { if (![...jobsCache.values()].some(j => j.status === "running")) $("#live-bar").style.width = "0%"; }, 1500);
    }
  }
}

/* ------------------------------------------------------------------ jobs tab */

let jobsRenderTimer = null;
function renderJobsSoon() {
  if (activeTab !== "jobs") return;
  clearTimeout(jobsRenderTimer);
  jobsRenderTimer = setTimeout(renderJobs, 250);
}

async function fetchJobs() {
  try {
    const list = await api.get("/api/jobs");
    for (const j of list) jobsCache.set(j.id, j);
  } catch {}
}

function renderJobs() {
  const list = [...jobsCache.values()].sort((a, b) => new Date(b.createdAt) - new Date(a.createdAt)).slice(0, 100);
  $("#jobs-list").innerHTML = list.length === 0
    ? `<p class="hint">No jobs yet.</p>`
    : list.map(j => `
      <div class="job">
        <span class="status ${esc(j.status)}">${esc(j.status)}</span>
        <span class="desc">${esc(j.description)}</span>
        <span>
          ${j.outputFile && j.status === "done" ? `<audio controls preload="none" src="${audioUrl(j.outputFile)}"></audio>` : ""}
          ${(j.status === "queued" || j.status === "running") ? `<button class="small danger" onclick="cancelJob('${j.id}')">cancel</button>` : ""}
        </span>
        <span class="meta">#${j.id} · ${esc(j.type)}${j.label ? " · " + esc(j.label) : ""} · ${fmtTime(j.createdAt)}${j.seconds ? ` · ${j.seconds.toFixed(1)}s audio` : ""}${j.tokens ? ` · ${j.tokens} tok` : ""}${j.status === "running" && j.detail ? ` · ${esc(j.detail)}` : ""}</span>
        ${j.error ? `<span class="err-msg">${esc(j.error)}</span>` : ""}
      </div>`).join("");
}

window.cancelJob = (id) => api.post(`/api/jobs/${id}/cancel`).catch(() => {});

/* ------------------------------------------------------------------ library */

const libSelection = new Set();
let libFiles = [];
const libObserver = new IntersectionObserver((entries) => {
  for (const en of entries) {
    if (en.isIntersecting && !en.target._loaded) {
      en.target._loaded = true;
      const f = en.target._file;
      en.target.appendChild(new WavePlayer(audioUrl(f.name), {
        compact: true, title: f.name,
        subtitle: `${f.seconds != null ? f.seconds.toFixed(1) + "s · " : ""}${fmtBytes(f.size)} · ${new Date(f.modified).toLocaleString()}`,
      }).el);
      libObserver.unobserve(en.target);
    }
  }
}, { rootMargin: "200px" });

async function refreshLibrary() {
  try { libFiles = await api.get("/api/outputs"); } catch { return; }
  renderLibrary();
}

function renderLibrary() {
  const q = $("#lib-search").value.trim().toLowerCase();
  const files = q ? libFiles.filter(f => f.name.toLowerCase().includes(q)) : libFiles;
  const totalSec = files.reduce((n, f) => n + (f.seconds || 0), 0);
  const totalBytes = files.reduce((n, f) => n + f.size, 0);
  $("#lib-stats").textContent = `${files.length} files · ${fmtDur(totalSec)} · ${fmtBytes(totalBytes)}${libSelection.size ? ` · ${libSelection.size} selected` : ""}`;

  const box = $("#lib-list");
  box.innerHTML = "";
  for (const f of files.slice(0, 100)) {
    const row = document.createElement("div");
    row.className = "lib-row" + (libSelection.has(f.name) ? " selected" : "");
    const cb = document.createElement("input");
    cb.type = "checkbox";
    cb.checked = libSelection.has(f.name);
    cb.onchange = () => {
      cb.checked ? libSelection.add(f.name) : libSelection.delete(f.name);
      row.classList.toggle("selected", cb.checked);
      renderLibraryStats();
    };
    const slot = document.createElement("div");
    slot.style.flex = "1";
    slot.style.minWidth = "0";
    slot._file = f;
    libObserver.observe(slot);
    const del = document.createElement("button");
    del.className = "small danger";
    del.textContent = "🗑";
    del.onclick = async () => { await api.del("/api/outputs/" + encodeURIComponent(f.name)); libSelection.delete(f.name); refreshLibrary(); };
    row.append(cb, slot, del);
    box.appendChild(row);
  }
}

function renderLibraryStats() {
  const q = $("#lib-search").value.trim().toLowerCase();
  const files = q ? libFiles.filter(f => f.name.toLowerCase().includes(q)) : libFiles;
  const totalSec = files.reduce((n, f) => n + (f.seconds || 0), 0);
  $("#lib-stats").textContent = `${files.length} files · ${fmtDur(totalSec)}${libSelection.size ? ` · ${libSelection.size} selected` : ""}`;
}

$("#lib-search").addEventListener("input", renderLibrary);
$("#lib-select-all").onclick = () => {
  const q = $("#lib-search").value.trim().toLowerCase();
  const files = q ? libFiles.filter(f => f.name.toLowerCase().includes(q)) : libFiles;
  if (libSelection.size === files.length) libSelection.clear();
  else files.forEach(f => libSelection.add(f.name));
  renderLibrary();
};
$("#lib-concat").onclick = async () => {
  if (libSelection.size < 2) { toast("select at least two files", { kind: "err" }); return; }
  const ordered = libFiles.filter(f => libSelection.has(f.name)).map(f => f.name).reverse(); // oldest first
  try {
    const r = await api.post("/api/outputs/concat", { files: ordered, gapSeconds: parseFloat($("#lib-gap").value) });
    toast(`✔ stitched ${ordered.length} files → ${r.name} (${r.seconds.toFixed(1)}s)`, { playUrl: audioUrl(r.name) });
    libSelection.clear();
    refreshLibrary();
  } catch (e) { toast("stitch failed: " + (e.error || ""), { kind: "err" }); }
};
$("#lib-delete").onclick = async () => {
  if (libSelection.size === 0) return;
  if (!confirm(`Delete ${libSelection.size} file(s)?`)) return;
  for (const name of libSelection) await api.del("/api/outputs/" + encodeURIComponent(name));
  libSelection.clear();
  refreshLibrary();
};

/* ------------------------------------------------------------------ speakers */

const expandedSpeakers = new Set();

async function refreshSpeakers() {
  let names;
  try { names = await api.get("/api/speakers"); } catch { return; }
  const box = $("#speakers-list");
  box.innerHTML = "";
  for (const n of names) {
    const card = document.createElement("div");
    card.className = "speaker-card";
    card.innerHTML = `
      <div class="speaker-head">
        <span class="sname">🗣 ${esc(n)}</span>
        <button class="small" data-act="use">use in studio</button>
        <button class="small" data-act="preview">▶ preview</button>
        ${n !== "default" ? `<button class="small danger" data-act="delete">delete</button>` : ""}
      </div>
      <div class="speaker-detail hidden"></div>`;
    const detail = card.querySelector(".speaker-detail");
    card.querySelector(".speaker-head").addEventListener("click", async (e) => {
      if (e.target.tagName === "BUTTON") return;
      const open = detail.classList.toggle("hidden");
      if (!open) { expandedSpeakers.add(n); await loadSpeakerDetail(n, detail); }
      else expandedSpeakers.delete(n);
    });
    card.querySelector('[data-act="use"]').onclick = () => { $("#gen-speaker").value = n; switchTab("studio"); };
    card.querySelector('[data-act="preview"]').onclick = async () => {
      await api.post(`/api/speakers/${encodeURIComponent(n)}/preview`);
      toast(`preview of '${n}' queued — appears in Jobs`, {});
    };
    const delBtn = card.querySelector('[data-act="delete"]');
    if (delBtn) delBtn.onclick = async () => {
      if (!confirm(`Delete speaker '${n}'?`)) return;
      await api.del(`/api/speakers/${encodeURIComponent(n)}`);
      refreshSpeakers(); refreshStatus();
    };
    box.appendChild(card);
    if (expandedSpeakers.has(n)) { detail.classList.remove("hidden"); loadSpeakerDetail(n, detail); }
  }
}

async function loadSpeakerDetail(name, el) {
  let info;
  try { info = await api.get(`/api/speakers/${encodeURIComponent(name)}/info`); } catch { return; }
  const gf = info.globalFeatures || {};
  const bar = (label, v) => `
    <div class="feat-bar"><span class="fb-name">${label}</span>
      <span class="fb-track"><span class="fb-fill" style="width:${v}%;display:block"></span></span>
      <span class="fb-val">${v}</span></div>`;
  el.innerHTML = `
    <div class="hint">“${esc(info.text.slice(0, 140))}${info.text.length > 140 ? "…" : ""}”</div>
    <div class="hint" style="margin:6px 0">${info.wordCount} words · ${info.totalSeconds}s of reference audio</div>
    ${bar("energy", gf.energy ?? 0)}${bar("spectral centroid", gf.spectralCentroid ?? 0)}${bar("pitch", gf.pitch ?? 0)}
    <div class="word-chips">${(info.words || []).slice(0, 24).map(w =>
      `<span class="word-chip"><b>${esc(w.word)}</b> ${w.duration}s</span>`).join("")}</div>`;
}

// ---- clone: upload vs record

let recordedBlob = null;
let recorder = null;

$("#clone-seg").addEventListener("click", (e) => {
  if (e.target.tagName !== "BUTTON") return;
  $$("#clone-seg button").forEach(b => b.classList.toggle("active", b === e.target));
  const rec = e.target.dataset.src === "record";
  $("#clone-upload").classList.toggle("hidden", rec);
  $("#clone-record").classList.toggle("hidden", !rec);
});

function drawLevel(level) {
  const canvas = $("#rec-meter");
  const g = canvas.getContext("2d");
  g.clearRect(0, 0, canvas.width, canvas.height);
  g.fillStyle = cssVar("--accent");
  const w = Math.min(1, level * 4) * canvas.width;
  g.fillRect(0, 8, w, canvas.height - 16);
}

$("#btn-record").onclick = async () => {
  const btn = $("#btn-record");
  if (!recorder || !recorder.recording) {
    try {
      recorder = new MicRecorder({
        onLevel: drawLevel,
        onTime: (t) => $("#rec-time").textContent = t.toFixed(1) + "s",
      });
      await recorder.start();
      btn.textContent = "■ stop";
      btn.classList.add("recording");
      $("#rec-preview").innerHTML = "";
    } catch (e) {
      toast("microphone unavailable: " + e.message, { kind: "err" });
    }
  } else {
    const { blob, seconds } = recorder.stop();
    btn.textContent = "● record";
    btn.classList.remove("recording");
    drawLevel(0);
    if (seconds < 1) { toast("recording too short", { kind: "err" }); return; }
    recordedBlob = blob;
    const url = URL.createObjectURL(blob);
    $("#rec-preview").innerHTML = "";
    $("#rec-preview").appendChild(new WavePlayer(url, { title: `recording (${seconds.toFixed(1)}s)`, compact: true }).el);
  }
};

$("#btn-clone").onclick = async () => {
  const recordMode = !$("#clone-record").classList.contains("hidden");
  const file = recordMode ? recordedBlob : $("#clone-file").files[0];
  const transcript = $("#clone-transcript").value.trim();
  const name = $("#clone-name").value.trim();
  const out = $("#clone-result");
  if (!file) { out.innerHTML = `<p class="statusline" style="color:var(--err)">${recordMode ? "record something first" : "choose a wav file"}</p>`; return; }
  if (!transcript) { out.innerHTML = `<p class="statusline" style="color:var(--err)">transcript is required</p>`; return; }

  const fd = new FormData();
  fd.append("audio", file, recordMode ? "recording.wav" : file.name);
  fd.append("transcript", transcript);
  fd.append("name", name || "my-voice");
  out.innerHTML = `<p class="statusline">uploading…</p>`;
  const r = await fetch("/api/speakers/clone", { method: "POST", body: fd });
  const j = await r.json();
  out.innerHTML = r.ok
    ? `<p class="statusline" style="color:var(--ok)">cloning '${esc(j.name)}' (job ${j.jobId}) — a voice preview lands in Jobs when done</p>`
    : `<p class="statusline" style="color:var(--err)">${esc(j.error || "failed")}</p>`;
};

/* ------------------------------------------------------------------ plugins */

let selectedPlugin = null;
let luaEditor = null;

function ensureEditor() {
  if (!luaEditor) {
    luaEditor = new LuaEditor($("#lua-editor"), {
      onSave: () => $("#btn-plugin-save").click(),
      onRun: () => $("#btn-plugin-run").click(),
    });
  }
  return luaEditor;
}

async function refreshPlugins() {
  ensureEditor();
  let list;
  try { list = await api.get("/api/plugins"); } catch { return; }
  $("#plugins-list").innerHTML = list.length === 0
    ? `<p class="hint">No .lua files yet — click “＋ new”.</p>`
    : list.map(p => `
      <div class="plugin-row ${p.name === selectedPlugin ? "selected" : ""}" data-name="${esc(p.name)}">
        <span class="dot ${p.loaded ? "on" : ""}" title="${p.loaded ? "loaded" : esc(p.error || "error")}"></span>
        <span class="pname">${esc(p.name)}</span>
        <button class="small" data-run>▶</button>
      </div>`).join("");
  $$("#plugins-list .plugin-row").forEach(row => {
    row.addEventListener("click", (e) => {
      if (e.target.hasAttribute("data-run")) { runPlugin(row.dataset.name); return; }
      selectPlugin(row.dataset.name);
    });
  });
}

async function selectPlugin(name) {
  selectedPlugin = name;
  $("#plugin-editor-title").innerHTML = `${esc(name)}.lua <span class="hint">Ctrl+S save · Ctrl+Enter run</span>`;
  try {
    const p = await api.get("/api/plugins/" + encodeURIComponent(name));
    ensureEditor().setValue(p.source);
    $("#plugin-output").textContent = (p.output || []).join("\n") + (p.error ? "\nLOAD ERROR: " + p.error : "");
  } catch {}
  refreshPlugins();
}

async function runPlugin(name) {
  selectedPlugin = name;
  await api.post(`/api/plugins/${encodeURIComponent(name)}/run`);
  $("#plugin-output").textContent = "queued…";
}

let pluginPollTimer = null;
function pollPluginOutput() {
  clearTimeout(pluginPollTimer);
  if (activeTab !== "plugins" || !selectedPlugin) return;
  api.get("/api/plugins/" + encodeURIComponent(selectedPlugin)).then(p => {
    const el = $("#plugin-output");
    const text = (p.output || []).join("\n");
    if (el.textContent !== text && text) { el.textContent = text; el.scrollTop = el.scrollHeight; }
  }).catch(() => {});
  pluginPollTimer = setTimeout(pollPluginOutput, 1500);
}

$("#btn-plugin-save").onclick = async () => {
  if (!selectedPlugin) { toast("select or create a plugin first", { kind: "err" }); return; }
  const j = await api.putText("/api/plugins/" + encodeURIComponent(selectedPlugin), ensureEditor().getValue());
  $("#plugin-output").textContent = j.loaded ? "saved & reloaded ✔" : "saved, but load failed:\n" + (j.error || "");
  refreshPlugins();
};

$("#btn-plugin-run").onclick = () => { if (selectedPlugin) runPlugin(selectedPlugin); };

$("#btn-plugin-delete").onclick = async () => {
  if (!selectedPlugin) return;
  if (!confirm(`Delete plugin '${selectedPlugin}'?`)) return;
  await api.del("/api/plugins/" + encodeURIComponent(selectedPlugin));
  selectedPlugin = null;
  ensureEditor().setValue("");
  $("#plugin-editor-title").textContent = "Editor";
  refreshPlugins();
};

$("#btn-new-plugin").onclick = async () => {
  const name = prompt("Plugin name (file will be plugins/<name>.lua):", "my-plugin");
  if (!name) return;
  const template = `-- ${name}.lua

function run()
    log.info("${name} starting")

    local r = tts.speak("Hello from ${name}!")
    log.info("wrote " .. r.name .. " (" .. string.format("%.1f", r.seconds) .. "s)")
end
`;
  await api.putText("/api/plugins/" + encodeURIComponent(name), template);
  await refreshPlugins();
  selectPlugin(name.replace(/[^A-Za-z0-9-_]/g, "-"));
};

$("#btn-reload-plugins").onclick = async () => { await api.post("/api/plugins/reload"); refreshPlugins(); };

/* ------------------------------------------------------------------ logs */

const logEntries = [];
let logSeq = 0;
let logFilter = "all";
let logsDirty = false;

function appendLog(e) {
  if (e.seq <= logSeq) return;
  logSeq = e.seq;
  logEntries.push(e);
  if (logEntries.length > 2000) logEntries.splice(0, logEntries.length - 2000);
  logsDirty = true;
}

let logRenderTimer = null;
function renderLogsSoon() {
  if (activeTab !== "logs") return;
  clearTimeout(logRenderTimer);
  logRenderTimer = setTimeout(renderLogs, 150);
}

function renderLogs() {
  if (!logsDirty && $("#log-view").childNodes.length > 0) return;
  logsDirty = false;
  const el = $("#log-view");
  el.innerHTML = "";
  for (const e of logEntries) {
    if (logFilter !== "all" && e.level !== logFilter) continue;
    const line = document.createElement("div");
    line.className = e.level === "error" ? "log-error" : e.level === "warn" ? "log-warn" : "";
    line.textContent = `${new Date(e.time).toLocaleTimeString()} [${e.level}] ${e.message}`;
    el.appendChild(line);
  }
  if ($("#log-scroll").checked) el.scrollTop = el.scrollHeight;
}

$("#log-filter").addEventListener("click", (e) => {
  if (e.target.tagName !== "BUTTON") return;
  logFilter = e.target.dataset.level;
  $$("#log-filter button").forEach(b => b.classList.toggle("active", b === e.target));
  logsDirty = true;
  renderLogs();
});
$("#log-clear").onclick = () => { logEntries.length = 0; logsDirty = true; renderLogs(); };

async function fetchLogs() {
  try {
    const entries = await api.get("/api/logs?after=" + logSeq);
    for (const e of entries) appendLog(e);
  } catch {}
}

/* ------------------------------------------------------------------ refresh loop */

function refreshTab(initial = false) {
  if (activeTab === "dashboard") { refreshOutputs(); refreshStats(); }
  if (activeTab === "jobs") { fetchJobs().then(renderJobs); }
  if (activeTab === "library") refreshLibrary();
  if (activeTab === "speakers") refreshSpeakers();
  if (activeTab === "plugins") { refreshPlugins(); pollPluginOutput(); }
  if (activeTab === "logs") { logsDirty = true; renderLogs(); }
}

setInterval(() => {
  refreshStatus();
  if (!sseOk) { fetchLogs().then(renderLogsSoon); fetchJobs().then(renderJobsSoon); }
  if (activeTab === "dashboard") refreshStats();
}, 5000);

connectSse();
refreshStatus();
fetchLogs();
fetchJobs();
refreshTab(true);
