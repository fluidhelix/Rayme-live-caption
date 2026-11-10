'use strict';
const { spawn, execFile } = require('node:child_process');
const { promisify } = require('node:util');
const { stopChild } = require('./whisper');
const run = promisify(execFile);
async function listDevices(exe) {
  const { stdout } = await run(exe, ['--list'], { windowsHide: true, timeout: 10000, maxBuffer: 256 * 1024 });
  const devices = JSON.parse(stdout);
  if (!Array.isArray(devices) || devices.length > 256 || devices.some(d => typeof d.id !== 'string' || typeof d.name !== 'string')) throw new Error('Invalid device list');
  return devices;
}
class Capture {
  constructor(exe, audio, status, discontinuity, spawnChild = spawn) { Object.assign(this, { exe, audio, status, discontinuity, spawnChild }); this.generation = 0; }
  async select(device = 'default') {
    const gen = ++this.generation; clearTimeout(this.retry); this.retry = null; const old = this.child; this.child = null;
    await stopChild(old); if (gen !== this.generation) return;
    this.discontinuity(); this.status('Connecting to playback device…');
    const args = typeof device === 'object' ? ['--process', String(device.pid), '--started', device.started] : ['--device', device];
    const child = this.spawnChild(this.exe, args, { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] }); this.child = child;
    child.stdout.on('data', data => { if (gen === this.generation) this.audio(data); });
    let log = '';
    child.stderr.on('data', data => {
      log += data.toString(); if (log.length > 16384) log = log.slice(-16384);
      let i; while ((i = log.indexOf('\n')) >= 0) { const line = log.slice(0, i); log = log.slice(i + 1); try { const event = JSON.parse(line); if (gen === this.generation) this.status(event.message || (event.type === 'ready' ? 'Listening to playback audio' : 'Audio unavailable'), event.type === 'ready' ? 'capturing' : 'unavailable'); } catch {} }
    });
    const reconnect = () => { if (gen !== this.generation || this.retry) return; this.generation++; this.discontinuity(); this.status('Audio device disconnected. Retrying selected device in 3 seconds; you can select another output.', 'unavailable'); this.retry = setTimeout(() => { this.retry = null; this.select(device); }, 3000); };
    child.once('error', () => { if (typeof device === 'object') { if (gen === this.generation) { this.generation++; this.discontinuity(); this.status('Application capture could not start. Refresh and select again.', 'unavailable'); } } else reconnect(); }); child.once('exit', code => {
      if (gen !== this.generation) return;
      if (typeof device === 'object' || code === 4) { this.generation++; this.discontinuity(); this.status('Source unavailable or activation failed. Refresh and explicitly select again.', 'unavailable'); return; }
      reconnect();
    });
  }
  async close() { this.generation++; clearTimeout(this.retry); this.retry = null; await stopChild(this.child); this.child = null; }
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
