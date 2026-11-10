'use strict';
const { EventEmitter } = require('node:events');
function wav(pcm) {
  if (pcm.length % 2) throw new Error('PCM must contain complete 16-bit samples');
  const h = Buffer.alloc(44);
  h.write('RIFF'); h.writeUInt32LE(36 + pcm.length, 4); h.write('WAVEfmt ', 8);
  h.writeUInt32LE(16, 16); h.writeUInt16LE(1, 20); h.writeUInt16LE(1, 22);
  h.writeUInt32LE(16000, 24); h.writeUInt32LE(32000, 28); h.writeUInt16LE(2, 32); h.writeUInt16LE(16, 34);
  h.write('data', 36); h.writeUInt32LE(pcm.length, 40); return Buffer.concat([h, pcm]);
}
// Fixed 20 ms frames make segmentation independent of pipe chunk boundaries.
class Segmenter extends EventEmitter {
  constructor({ threshold = 80, maxMs = 15000, pauseMs = 900, partialMs = 2500 } = {}) {
    super(); Object.assign(this, { threshold, maxMs, pauseMs, partialMs }); this.id = 0; this.reset();
  }
  reset() { this.tail = Buffer.alloc(0); this.pre = []; this.frames = []; this.quiet = 0; this.voiced = 0; this.lastPartial = 0; }
  push(chunk) {
    const data = Buffer.concat([this.tail, chunk]);
    let i = 0;
    for (; i + 640 <= data.length; i += 640) this.frame(Buffer.from(data.subarray(i, i + 640)));
    this.tail = Buffer.from(data.subarray(i));
  }
  frame(frame) {
    let power = 0;
    for (let i = 0; i < 640; i += 2) power += frame.readInt16LE(i) ** 2;
    const active = Math.sqrt(power / 320) >= this.threshold;
    if (!this.frames.length) {
      if (!active) { this.pre.push(frame); if (this.pre.length > 12) this.pre.shift(); return; }
      this.id++; this.frames = this.pre; this.pre = [];
    }
    this.frames.push(frame); this.quiet = active ? 0 : this.quiet + 20; if (active) this.voiced += 20;
    const ms = this.frames.length * 20;
    if (ms >= this.maxMs || this.quiet >= this.pauseMs) { this.finish(); return; }
    if (ms >= 1200 && ms - this.lastPartial >= this.partialMs) {
      this.lastPartial = ms; this.emit('segment', { id: this.id, pcm: Buffer.concat(this.frames), final: false });
    }
  }
  finish() {
    if (this.voiced >= 200) this.emit('segment', { id: this.id, pcm: Buffer.concat(this.frames), final: true });
    this.frames = []; this.quiet = 0; this.voiced = 0; this.lastPartial = 0;
  }
}
class Scheduler {
  constructor(infer, publish, status) { Object.assign(this, { infer, publish, status }); this.finals = []; this.partial = null; this.busy = false; this.closed = false; this.epoch = 0; this.finalized = 0; }
  add(job) {
    if (this.closed) return;
    if (job.final) {
      this.finalized = Math.max(this.finalized, job.id);
      if (this.partial?.id <= job.id) this.partial = null;
      if (this.finals.length === 2) { this.finals.shift(); this.status('Audio is arriving faster than transcription; an older segment was skipped.'); }
      this.finals.push(job);
    } else if (job.id > this.finalized) this.partial = job;
    this.pump();
  }
  async pump() {
    if (this.busy || this.closed) return;
    const job = this.finals.shift() || this.partial;
    if (!job) return;
    if (job === this.partial) this.partial = null;
    this.busy = true; const epoch = this.epoch; this.controller = new AbortController();
    try {
      const text = await this.infer(job.pcm, this.controller.signal);
      if (!this.closed && epoch === this.epoch && (job.final || job.id > this.finalized)) this.publish({ type: 'caption', id: job.id, final: job.final, text });
    } catch (e) { if (!this.closed && epoch === this.epoch) this.status('Transcription failed. Check the local Whisper server; retrying with new audio.'); }
    finally { this.busy = false; this.controller = null; this.pump(); }
  }
  reset() { this.epoch++; this.controller?.abort(); this.finals = []; this.partial = null; }
  close() { this.closed = true; this.reset(); }
}
module.exports = { wav, Segmenter, Scheduler };
