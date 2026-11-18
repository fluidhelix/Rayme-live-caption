'use strict';
const { spawn, execFile } = require('node:child_process');
const { promisify } = require('node:util');
const { stopChild } = require('./whisper');
const run = promisify(execFile);
const DEFAULT_CHANGED = 5; // LoopbackCapture DefaultEndpointChange.ExitCode
const RETRY_MS = 3000;
const SWITCH_MS = 150;
// Healthy helpers emit every 100 ms, padding silence. Allow more than the
// native two-second buffer before treating a missing PCM stream as stalled.
const PCM_STALL_MS = 3000;
async function listDevices(exe) {
  const { stdout } = await run(exe, ['--list'], { windowsHide: true, timeout: 10000, maxBuffer: 256 * 1024 });
  const devices = JSON.parse(stdout);
  if (!Array.isArray(devices) || devices.length > 256 || devices.some(d => typeof d.id !== 'string' || typeof d.name !== 'string')) throw new Error('Invalid device list');
  return devices;
}
class Capture {
  constructor(exe, audio, status, discontinuity, spawnChild = spawn, { setTimer = setTimeout, clearTimer = clearTimeout, elapsedNow = () => performance.now() } = {}) {
    Object.assign(this, { exe, audio, status, discontinuity, spawnChild, setTimer, clearTimer, elapsedNow });
    this.generation = 0;
  }
  async select(device = 'default') {
    const gen = this.invalidate();
    await this.stopWorker();
    if (gen !== this.generation) return;
    this.discontinuity();
    this.connect(device, gen);
  }
  connect(device, gen) {
    this.status('Connecting to playback device…', 'recovering');
    const args = typeof device === 'object' ? ['--process', String(device.pid), '--started', device.started] : ['--device', device];
    const child = this.spawnChild(this.exe, args, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] }); this.child = child;
    child.stdin?.on('error', () => {}); // A worker can exit before reading EOF.
    let lastPcmAt;
    const recover = async () => {
      const recovery = this.invalidate();
      this.discontinuity(device === 'default');
      this.status('Audio capture stalled. Reconnecting to the selected source…', 'recovering');
      try {
        await this.stopWorker();
        if (recovery === this.generation) this.connect(device, recovery);
      } catch {
        if (recovery === this.generation) this.status('Audio capture could not recover. Select the source again.', 'unavailable');
      }
    };
    const checkPcm = () => {
      if (gen !== this.generation) return;
      const remaining = PCM_STALL_MS - (this.elapsedNow() - lastPcmAt);
      if (remaining <= 0) { void recover(); return; }
      this.watchdog = this.setTimer(checkPcm, remaining);
      this.watchdog?.unref?.();
    };
    const startWatch = () => {
      if (lastPcmAt !== undefined) return;
      lastPcmAt = this.elapsedNow();
      checkPcm();
    };
    child.stdout.on('data', data => {
      if (gen !== this.generation || !data.length) return;
      const now = this.elapsedNow();
      // After resume, queued pipe I/O may reach Node before the overdue timer.
      // Raw PCM has no timestamp to distinguish that backlog from new audio.
      if (lastPcmAt !== undefined && now - lastPcmAt >= PCM_STALL_MS) { void recover(); return; }
      startWatch();
      lastPcmAt = now;
      this.audio(data);
    });
    let log = '';
    child.stderr.on('data', data => {
      log += data.toString(); if (log.length > 16384) log = log.slice(-16384);
      let i; while ((i = log.indexOf('\n')) >= 0) {
        const line = log.slice(0, i); log = log.slice(i + 1);
        try {
          const event = JSON.parse(line);
          if (gen !== this.generation) continue;
          if (event.type === 'ready') startWatch();
          this.status(event.message || (event.type === 'ready' ? 'Listening to playback audio' : 'Audio unavailable'), event.type === 'ready' ? 'capturing' : 'unavailable');
        } catch {}
      }
    });
    const reconnect = (switching = false) => {
      if (gen !== this.generation) return;
      const retryGen = this.invalidate();
      // If the default disappears before the notification arrives, recovery
      // may still resolve a different endpoint. Discard its open partial too.
      this.discontinuity(device === 'default');
      this.status(switching ? 'Windows default playback changed. Reconnecting to the current output…'
        : 'Audio device disconnected. Retrying selected device in 3 seconds; you can select another output.', switching ? 'recovering' : 'unavailable');
      this.retry = this.setTimer(() => {
        if (retryGen !== this.generation) return;
        this.retry = null;
        // exit/error is terminal for this helper; stdout from its old pipe
        // remains fenced off by generation even if it arrives after exit.
        this.connect(device, retryGen);
      }, switching ? SWITCH_MS : RETRY_MS);
    };
    child.once('error', () => { if (typeof device === 'object') { if (gen === this.generation) { this.invalidate(); this.discontinuity(); this.status('Application capture could not start. Refresh and select again.', 'unavailable'); } } else reconnect(); }); child.once('exit', code => {
      if (gen !== this.generation) return;
      if (typeof device === 'object' || code === 4) { this.invalidate(); this.discontinuity(); this.status('Source unavailable or activation failed. Refresh and explicitly select again.', 'unavailable'); return; }
      reconnect(device === 'default' && code === DEFAULT_CHANGED);
    });
  }
  invalidate() {
    const gen = ++this.generation;
    this.clearTimer(this.retry);
    this.retry = null;
    this.clearTimer(this.watchdog);
    this.watchdog = null;
    return gen;
  }
  async stopWorker() {
    if (this.stopping) return this.stopping;
    const child = this.child;
    if (!child) return;
    this.stopping = (async () => {
      if (child.exitCode === null && !child.signalCode && child.stdin?.writable) {
        // Give the helper time to dispose the recorder and unregister its Core
        // Audio listener. Keep the existing bounded kill fallback for a hang.
        await new Promise(resolve => {
          const finish = () => { clearTimeout(timeout); child.off('exit', finish); child.off('error', finish); resolve(); };
          const timeout = setTimeout(finish, 1000);
          child.once('exit', finish);
          child.once('error', finish);
          child.stdin.end();
        });
      }
      await stopChild(child);
      if (child.exitCode === null && !child.signalCode) throw Error('Audio capture worker did not exit.');
      this.child = null;
    })().finally(() => { this.stopping = null; });
    return this.stopping;
  }
  async close() { this.invalidate(); await this.stopWorker(); }
}
async function listApplications(exe) {
  const { stdout } = await run(exe, ['--apps'], { windowsHide: true, timeout: 10000, maxBuffer: 256 * 1024 });
  return validateApplications(JSON.parse(stdout));
}
function validateApplications(data) {
  if (typeof data.supported !== 'boolean' || !Array.isArray(data.apps) || data.apps.length > 512 || data.apps.some(a => !Number.isInteger(a.pid) || a.pid <= 0 || typeof a.started !== 'string' || !/^\d+$/.test(a.started) || a.id !== `${a.pid}:${a.started}` || typeof a.name !== 'string' || a.name.length > 260 || (a.ancestors !== undefined && (!Array.isArray(a.ancestors) || a.ancestors.length > 64 || a.ancestors.some(v => typeof v !== 'string' || !/^\d+:\d+$/.test(v)))))) throw Error('Invalid application discovery response');
  return { supported: data.supported, apps: [...new Map(data.apps.map(a => [a.id, Object.freeze({ ...a, kind: 'process' })])).values()] };
}
module.exports = { Capture, listDevices, listApplications, validateApplications };
