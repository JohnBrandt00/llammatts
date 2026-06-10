"use strict";

/* Canvas waveform player. Usage: const p = new WavePlayer(url, {title, subtitle, compact}); parent.append(p.el); */

const Wave = (() => {
  let ctx = null;
  const peakCache = new Map(); // url -> Promise<{peaks: Float32Array, duration: number}>
  const COLUMNS = 400;

  function audioCtx() {
    if (!ctx) ctx = new (window.AudioContext || window.webkitAudioContext)();
    return ctx;
  }

  function getPeaks(url) {
    if (peakCache.has(url)) return peakCache.get(url);
    const promise = (async () => {
      const buf = await fetch(url).then(r => {
        if (!r.ok) throw new Error("fetch failed");
        return r.arrayBuffer();
      });
      const audio = await audioCtx().decodeAudioData(buf);
      const data = audio.getChannelData(0);
      const peaks = new Float32Array(COLUMNS * 2);
      const step = data.length / COLUMNS;
      for (let c = 0; c < COLUMNS; c++) {
        let min = 0, max = 0;
        const start = Math.floor(c * step), end = Math.min(data.length, Math.floor((c + 1) * step));
        for (let i = start; i < end; i++) {
          if (data[i] < min) min = data[i];
          if (data[i] > max) max = data[i];
        }
        peaks[c * 2] = min;
        peaks[c * 2 + 1] = max;
      }
      return { peaks, duration: audio.duration };
    })();
    promise.catch(() => peakCache.delete(url));
    peakCache.set(url, promise);
    return promise;
  }

  return { getPeaks, audioCtx };
})();

const _wavePlayers = new Set();

function cssVar(name) {
  return getComputedStyle(document.documentElement).getPropertyValue(name).trim();
}

class WavePlayer {
  constructor(url, opts = {}) {
    this.url = url;
    this.opts = opts;
    this.audio = null;
    this.data = null;
    this.progress = 0;
    this._raf = null;

    const el = document.createElement("div");
    el.className = "wave-player" + (opts.compact ? " compact" : "");
    el.innerHTML = `
      <button class="wp-btn" title="play / pause">▶</button>
      <div class="wp-main">
        ${opts.title ? `<div class="wp-title-row"><span class="wp-title">${opts.title}</span><span class="wp-sub">${opts.subtitle || ""}</span></div>` : ""}
        <canvas class="wp-canvas" height="${opts.compact ? 32 : 44}"></canvas>
      </div>
      <span class="wp-time">·</span>
      <a class="wp-dl" href="${url}" download title="download">⬇</a>`;
    this.el = el;
    this.btn = el.querySelector(".wp-btn");
    this.canvas = el.querySelector(".wp-canvas");
    this.timeEl = el.querySelector(".wp-time");

    this.btn.addEventListener("click", () => this.toggle());
    this.canvas.addEventListener("click", (e) => {
      const audio = this.ensureAudio();
      if (this.data && audio.duration) {
        const rect = this.canvas.getBoundingClientRect();
        audio.currentTime = (e.clientX - rect.left) / rect.width * audio.duration;
        this.progress = audio.currentTime / audio.duration;
        this.draw();
      }
    });

    _wavePlayers.add(this);
    this.load();
  }

  async load() {
    try {
      this.data = await Wave.getPeaks(this.url);
      this.timeEl.textContent = fmtSec(this.data.duration);
      this.draw();
    } catch {
      this.timeEl.textContent = "?";
    }
  }

  ensureAudio() {
    if (!this.audio) {
      this.audio = new Audio(this.url);
      this.audio.addEventListener("ended", () => { this.btn.textContent = "▶"; this.progress = 0; this.draw(); this.updateTime(); });
      this.audio.addEventListener("pause", () => { this.btn.textContent = "▶"; });
      this.audio.addEventListener("play", () => {
        this.btn.textContent = "⏸";
        // stop any other playing player
        for (const p of _wavePlayers) if (p !== this && p.audio && !p.audio.paused) p.audio.pause();
        this.tick();
      });
    }
    return this.audio;
  }

  toggle() {
    const audio = this.ensureAudio();
    if (audio.paused) audio.play().catch(() => {});
    else audio.pause();
  }

  tick() {
    cancelAnimationFrame(this._raf);
    const step = () => {
      if (!this.audio) return;
      if (this.audio.duration) this.progress = this.audio.currentTime / this.audio.duration;
      this.draw();
      this.updateTime();
      if (!this.audio.paused) this._raf = requestAnimationFrame(step);
    };
    this._raf = requestAnimationFrame(step);
  }

  updateTime() {
    if (!this.data) return;
    const cur = this.audio && !this.audio.paused ? this.audio.currentTime : null;
    this.timeEl.textContent = cur != null ? `${fmtSec(cur)} / ${fmtSec(this.data.duration)}` : fmtSec(this.data.duration);
  }

  draw() {
    const canvas = this.canvas;
    const dpr = window.devicePixelRatio || 1;
    const w = canvas.clientWidth || 300;
    const h = canvas.getAttribute("height") | 0;
    if (canvas.width !== Math.floor(w * dpr)) {
      canvas.width = Math.floor(w * dpr);
      canvas.height = Math.floor(h * dpr);
    }
    const g = canvas.getContext("2d");
    g.clearRect(0, 0, canvas.width, canvas.height);
    if (!this.data) return;

    const { peaks } = this.data;
    const columns = peaks.length / 2;
    const colPlayed = cssVar("--accent") || "#f0a050";
    const colRest = cssVar("--wave") || "#3a4150";
    const mid = canvas.height / 2;
    const barW = canvas.width / columns;
    const playedCols = Math.floor(this.progress * columns);

    for (let c = 0; c < columns; c++) {
      const min = peaks[c * 2], max = peaks[c * 2 + 1];
      let y0 = mid + min * mid * 0.92;
      let y1 = mid + max * mid * 0.92;
      if (y1 - y0 < 1.5 * dpr) { y0 = mid - 0.75 * dpr; y1 = mid + 0.75 * dpr; }
      g.fillStyle = c <= playedCols && this.progress > 0 ? colPlayed : colRest;
      g.fillRect(c * barW, y0, Math.max(1, barW * 0.7), y1 - y0);
    }

    if (this.progress > 0) {
      g.fillStyle = colPlayed;
      g.fillRect(this.progress * canvas.width - 1, 0, 2 * dpr, canvas.height);
    }
  }

  destroy() {
    cancelAnimationFrame(this._raf);
    if (this.audio) { this.audio.pause(); this.audio.src = ""; }
    _wavePlayers.delete(this);
    this.el.remove();
  }
}

function redrawAllWaves() {
  for (const p of _wavePlayers) p.draw();
}

function fmtSec(s) {
  if (s == null || !isFinite(s)) return "·";
  const m = Math.floor(s / 60), sec = Math.floor(s % 60);
  return `${m}:${sec.toString().padStart(2, "0")}`;
}
