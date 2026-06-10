"use strict";

/* Microphone recorder producing a 16-bit mono WAV blob (works offline; localhost is a secure context). */

class MicRecorder {
  constructor({ onLevel, onTime } = {}) {
    this.onLevel = onLevel;
    this.onTime = onTime;
    this.recording = false;
    this.chunks = [];
    this.sampleRate = 48000;
  }

  async start() {
    this.stream = await navigator.mediaDevices.getUserMedia({
      audio: { channelCount: 1, echoCancellation: false, noiseSuppression: false, autoGainControl: false },
    });
    this.ctx = new (window.AudioContext || window.webkitAudioContext)();
    this.sampleRate = this.ctx.sampleRate;
    this.source = this.ctx.createMediaStreamSource(this.stream);
    this.proc = this.ctx.createScriptProcessor(4096, 1, 1);
    this.chunks = [];
    this.startedAt = performance.now();
    this.recording = true;

    this.proc.onaudioprocess = (e) => {
      if (!this.recording) return;
      const input = e.inputBuffer.getChannelData(0);
      this.chunks.push(new Float32Array(input));
      if (this.onLevel) {
        let sum = 0;
        for (let i = 0; i < input.length; i++) sum += input[i] * input[i];
        this.onLevel(Math.sqrt(sum / input.length));
      }
      if (this.onTime) this.onTime((performance.now() - this.startedAt) / 1000);
    };

    this.source.connect(this.proc);
    this.proc.connect(this.ctx.destination); // required for onaudioprocess to fire in some browsers
  }

  stop() {
    this.recording = false;
    try { this.proc.disconnect(); this.source.disconnect(); } catch {}
    try { this.stream.getTracks().forEach(t => t.stop()); } catch {}
    try { this.ctx.close(); } catch {}

    const total = this.chunks.reduce((n, c) => n + c.length, 0);
    const merged = new Float32Array(total);
    let offset = 0;
    for (const c of this.chunks) { merged.set(c, offset); offset += c.length; }
    this.chunks = [];

    return {
      blob: encodeWavBlob(merged, this.sampleRate),
      seconds: total / this.sampleRate,
      sampleRate: this.sampleRate,
    };
  }
}

function encodeWavBlob(samples, sampleRate) {
  const buffer = new ArrayBuffer(44 + samples.length * 2);
  const view = new DataView(buffer);
  const writeStr = (off, s) => { for (let i = 0; i < s.length; i++) view.setUint8(off + i, s.charCodeAt(i)); };

  writeStr(0, "RIFF");
  view.setUint32(4, 36 + samples.length * 2, true);
  writeStr(8, "WAVE");
  writeStr(12, "fmt ");
  view.setUint32(16, 16, true);
  view.setUint16(20, 1, true);        // PCM
  view.setUint16(22, 1, true);        // mono
  view.setUint32(24, sampleRate, true);
  view.setUint32(28, sampleRate * 2, true);
  view.setUint16(32, 2, true);
  view.setUint16(34, 16, true);
  writeStr(36, "data");
  view.setUint32(40, samples.length * 2, true);

  let off = 44;
  for (let i = 0; i < samples.length; i++, off += 2) {
    const s = Math.max(-1, Math.min(1, samples[i]));
    view.setInt16(off, Math.round(s * 32767), true);
  }
  return new Blob([view], { type: "audio/wav" });
}
