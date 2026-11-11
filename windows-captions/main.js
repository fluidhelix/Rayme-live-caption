'use strict';
require('dotenv').config();
const path = require('node:path');
const fs = require('node:fs');
const { launchOverlay } = require('./native-overlay');
const { SourceScheduler, SourceManager } = require('./sources');
const { Whisper, resolveRuntime } = require('./whisper');
const { listDevices, listApplications } = require('./capture');
const { createUI } = require('./server');
async function main() {
  if (process.platform !== 'win32') throw new Error('Playback capture requires Windows; application mode targets Windows 11 x64.');
  const helper = path.join(__dirname, '..', 'native', 'bin', 'LoopbackCapture.exe');
  if (!fs.existsSync(helper)) throw new Error('Missing audio helper. Download windows-loopback-win-x64 from GitHub Actions into native\\bin. See WINDOWS-CAPTIONS.md.');
  const whisper = new Whisper(resolveRuntime());
  let ui, overlay, manager, scheduler, stopped = false, controls = Promise.resolve();
  // Serialize selection/pause commands; no competing worker creation during rapid UI changes.
  const control = task => { controls = controls.catch(() => {}).then(() => { if (!stopped) return task(); }); return controls; };
  const shutdown = async () => { if (stopped) return; stopped = true; await controls.catch(() => {}); await overlay?.close(); scheduler?.close(); await manager?.close(); await whisper.close(); await ui?.close(); };
  const signal = () => shutdown().then(() => process.exit(0));
  process.once('SIGINT', signal); process.once('SIGTERM', signal);
  try {
    const threshold = Number(process.env.WINDOWS_SILENCE_RMS || 80);
    if (!Number.isFinite(threshold) || threshold < 0 || threshold > 32767) throw new Error('WINDOWS_SILENCE_RMS must be 0–32767.');
    const refresh = async () => {
      ui.publish({ type: 'devices', devices: await listDevices(helper), selected: process.env.WINDOWS_AUDIO_DEVICE || 'default' });
      try { const result = await listApplications(helper); ui.publish({ type: 'applications', ...result }); }
      catch { ui.publish({ type: 'status', message: 'Application discovery unavailable. Whole-device capture remains available; install the Phase 2 helper.' }); }
    };
    ui = await createUI({ port: Number(process.env.WINDOWS_CAPTION_PORT || 8090), onRefresh: () => control(refresh),
      onConfigure: (slot, descriptor) => control(async () => { if (!manager) throw Error('Wait for Whisper readiness'); await manager.configure(slot, descriptor); }),
      onPause: paused => control(async () => { if (!manager) throw Error('Wait for Whisper readiness'); await manager.pause(paused); }) });
    overlay = launchOverlay({ url: ui.url, publish: ui.publish });
    await overlay.ready;
    if (stopped) return;
    console.log('Caption window opened. Ctrl+C stops all capture workers and Whisper.');
    await whisper.start(message => ui.publish({ type: 'status', message }));
    if (stopped) return;
    scheduler = new SourceScheduler((pcm, signal) => whisper.infer(pcm, signal), event => ui.publish(event), (source, state, message) => ui.publish({ type: 'source-status', source, state, message }));
    manager = new SourceManager({ helper, scheduler, publish: event => ui.publish(event), threshold });
    await refresh();
    ui.publish({ type: 'status', message: 'Whisper ready. Enable up to two applications, or enable whole-device fallback. Nothing is captured until enabled.' });
  } catch (e) { await shutdown(); throw e; }
}
if (require.main === module) main().catch(e => { console.error(e.message); process.exitCode = 1; });
module.exports = { main };
