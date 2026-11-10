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
    const child = this.spawnChild(this.exe, ['--device', device], { windowsHide: true, stdio: ['pipe', 'pipe', 'pipe'] }); this.child = child;
    child.stdout.on('data', data => { if (gen === this.generation) this.audio(data); });
    let log = '';
    child.stderr.on('data', data => {
      log += data.toString(); if (log.length > 16384) log = log.slice(-16384);
      let i; while ((i = log.indexOf('\n')) >= 0) { const line = log.slice(0, i); log = log.slice(i + 1); try { const event = JSON.parse(line); if (gen === this.generation) this.status(event.type === 'ready' ? 'Listening to playback audio' : 'Audio device unavailable; reconnecting…'); } catch {} }
    });
    const reconnect = () => { if (gen !== this.generation || this.retry) return; this.discontinuity(); this.status('Audio device disconnected. Retrying selected device in 3 seconds; you can select another output.'); this.retry = setTimeout(() => { this.retry = null; this.select(device); }, 3000); };
    child.once('error', reconnect); child.once('exit', reconnect);
  }
  async close() { this.generation++; clearTimeout(this.retry); this.retry = null; await stopChild(this.child); this.child = null; }
}
module.exports = { Capture, listDevices };
