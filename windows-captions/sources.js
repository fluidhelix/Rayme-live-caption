'use strict';
const { Segmenter } = require('./audio');
const { Capture } = require('./capture');

// One model request globally. Every source owns its queues, final watermark and generation.
class SourceScheduler {
  constructor(infer, publish, status, now = Date.now) {
    Object.assign(this, { infer, publish, status, now }); this.sources = new Map(); this.last = null; this.busy = false; this.closed = false;
  }
  attach(source) { this.sources.set(source.id, { source: Object.freeze({ ...source }), finals: [], partial: null, finalized: 0 }); }
  remove(id) { this.sources.delete(id); }
  add(source, job) {
    const s = this.sources.get(source.id);
    if (this.closed || !s || s.source.generation !== source.generation) return;
    const item = { ...job, source: s.source, queuedAt: this.now() };
    if (job.final) {
      s.finalized = Math.max(s.finalized, job.id); s.partial = null;
      if (s.finals.length === 2) { s.finals.shift(); this.status(source, 'delayed', 'Overloaded: an older finalized segment was skipped.'); }
      s.finals.push(item);
    } else if (job.id > s.finalized) s.partial = item;
    this.pump();
  }
  async pump() {
    if (this.busy || this.closed) return;
    const keys = [...this.sources.keys()]; const start = (keys.indexOf(this.last) + 1) % (keys.length || 1);
    const order = keys.slice(start).concat(keys.slice(0, start));
    // Drop/rewrite partial work first; finals get round-robin service across sources.
    let key = order.find(k => this.sources.get(k).finals.length);
    if (!key) key = order.find(k => this.sources.get(k).partial);
    if (!key) return;
    const state = this.sources.get(key), job = state.finals.shift() || state.partial;
    if (job === state.partial) state.partial = null;
    this.last = key; this.busy = true; this.controller = new AbortController();
    const started = this.now(), queueAgeMs = started - job.queuedAt;
    if (queueAgeMs > 3000) this.status(job.source, 'delayed', `Transcription delayed ${Math.round(queueAgeMs / 1000)} seconds.`);
    try {
      const text = await this.infer(job.pcm, this.controller.signal);
      if (!this.closed && this.sources.get(key) === state && (job.final || job.id > state.finalized)) {
        this.publish({ type: 'caption', source: job.source, id: job.id, final: job.final, text, capturedAt: job.capturedAt, inferenceMs: this.now() - started, queueAgeMs });
        this.status(job.source, state.finals.length ? 'delayed' : 'capturing', `Last inference ${this.now() - started} ms; queue age ${queueAgeMs} ms${state.finals.length ? '; finalized audio is pending.' : '.'}`);
      }
    } catch {
      if (!this.closed && this.sources.get(key) === state) {
        this.status(job.source, 'unavailable', 'Local transcription failed; new audio will be retried.');
        this.publish({ type: 'reset', source: job.source });
      }
    } finally { this.busy = false; this.controller = null; this.pump(); }
  }
  close() { this.closed = true; this.sources.clear(); this.controller?.abort(); }
}
class SourceManager {
  constructor({ helper, scheduler, publish, threshold = 80, createCapture, now = Date.now }) {
    Object.assign(this, { helper, scheduler, publish, threshold, now });
    this.createCapture = createCapture || ((audio, status, reset) => new Capture(helper, audio, status, reset));
    this.slots = new Map(); this.generation = 0; this.paused = false; this.closed = false;
  }
  status(source, state, message) { this.publish({ type: 'source-status', source, state, message }); }
  async remove(slot) {
    const entry = this.slots.get(slot); if (!entry) return;
    this.slots.delete(slot); this.scheduler.remove(entry.source.id);
    this.publish({ type: 'source-removed', source: entry.source }); await entry.capture?.close();
  }
  async configure(slot, descriptor) {
    if (this.closed) return;
    if (!['a', 'b', 'device'].includes(slot)) throw Error('Unknown source slot');
    if (!descriptor) { await this.remove(slot); return; }
    if ((slot === 'device') !== (descriptor.kind === 'device')) throw Error('Source mode mismatch');
    if (descriptor.kind === 'process' && (!Number.isInteger(descriptor.pid) || !/^\d+$/.test(descriptor.started))) throw Error('Invalid process identity');
    for (const [other, entry] of this.slots) {
      if (other !== slot && entry.descriptor.kind === 'process' && descriptor.kind === 'process' && (entry.descriptor.ancestors?.includes(descriptor.id) || descriptor.ancestors?.includes(entry.descriptor.id))) throw Error('Selected application process trees overlap. Choose independent sources.');
      if (other !== slot && entry.descriptor.kind === descriptor.kind && entry.descriptor.id === descriptor.id) throw Error('That application is already selected.');
    }
    for (const other of [...this.slots.keys()]) if (other === slot || (slot === 'device' ? other !== 'device' : other === 'device')) await this.remove(other);
    const source = Object.freeze({ id: slot, name: descriptor.name, identity: descriptor.id, generation: ++this.generation });
    const entry = { descriptor: Object.freeze({ ...descriptor }), source, capture: null, segmenter: null };
    this.slots.set(slot, entry);
    if (!this.paused) await this.start(entry); else this.status(source, 'stopped', 'Paused');
  }
  async start(entry) {
    const reset = () => {
      this.scheduler.remove(entry.source.id);
      entry.source = Object.freeze({ ...entry.source, generation: ++this.generation });
      entry.segmenter = new Segmenter({ threshold: this.threshold });
      // PCM byte offset + capture-worker activation time gives approximate capture order.
      // Raw PCM pipe has no WASAPI QPC header; never claim sample-accurate cross-app timing.
      const anchor = this.now();
      entry.segmenter.on('segment', job => this.scheduler.add(entry.source, { ...job, capturedAt: anchor + job.captureOffsetMs }));
      entry.push = pcm => entry.segmenter.push(pcm);
      this.scheduler.attach(entry.source); this.publish({ type: 'reset', source: entry.source });
    };
    entry.capture = this.createCapture(pcm => { if (!this.paused && this.slots.get(entry.source.id) === entry) entry.push?.(pcm); },
      (message, state = 'waiting') => { if (this.slots.get(entry.source.id) === entry) this.status(entry.source, state, message); },
      () => { if (this.slots.get(entry.source.id) === entry && !this.paused) reset(); });
    reset();
    await entry.capture.select(entry.descriptor.kind === 'device' ? entry.descriptor.endpoint : entry.descriptor);
  }
  async pause(value) {
    if (this.closed || this.paused === value) return;
    this.paused = value;
    for (const entry of this.slots.values()) {
      this.scheduler.remove(entry.source.id); this.publish({ type: 'reset', source: entry.source });
      if (value) { await entry.capture?.close(); entry.capture = null; this.status(entry.source, 'stopped', 'Paused — no capture worker is running'); }
      else await this.start(entry);
    }
    this.publish({ type: 'paused', paused: value });
  }
  async close() { this.closed = true; for (const slot of [...this.slots.keys()]) await this.remove(slot); }
}
module.exports = { SourceScheduler, SourceManager };
