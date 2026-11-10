'use strict';
require('dotenv').config();
const path = require('node:path');
const fs = require('node:fs');
const { spawn } = require('node:child_process');
const { Segmenter, Scheduler } = require('./audio');
const { Whisper, resolveRuntime } = require('./whisper');
const { Capture, listDevices } = require('./capture');
const { createUI } = require('./server');
async function main() {
  if (process.platform !== 'win32') throw new Error('Playback capture requires Windows 10/11. Run tests on other systems with npm test.');
  const helper = path.join(__dirname, '..', 'native', 'bin', 'LoopbackCapture.exe');
  if (!fs.existsSync(helper)) throw new Error('Missing audio helper. Download windows-loopback-win-x64 from GitHub Actions and extract LoopbackCapture.exe into native\\bin. See WINDOWS-CAPTIONS.md.');
  const whisper = new Whisper(resolveRuntime());
  let ui, capture, scheduler, stopped = false;
  const shutdown = async () => { if (stopped) return; stopped = true; scheduler?.close(); await capture?.close(); await whisper.close(); await ui?.close(); };
  const signal = () => shutdown().then(() => process.exit(0));
  process.once('SIGINT', signal); process.once('SIGTERM', signal);
  try {
    const threshold = Number(process.env.WINDOWS_SILENCE_RMS || 80);
    if (!Number.isFinite(threshold) || threshold < 0 || threshold > 32767) throw new Error('WINDOWS_SILENCE_RMS must be 0–32767.');
    const segmenter = new Segmenter({ threshold });
    let selected = process.env.WINDOWS_AUDIO_DEVICE || 'default';
    const status = message => ui.publish({ type: 'status', message });
    const refresh = async () => ui.publish({ type: 'devices', devices: await listDevices(helper), selected });
    ui = await createUI({ port: Number(process.env.WINDOWS_CAPTION_PORT || 8090), onRefresh: refresh, onSelect: async id => { selected = id; await capture.select(id); await refresh(); } });
    // Browser is an ordinary external window. No game integration or graphics hooks.
    const browser = spawn('rundll32.exe', ['url.dll,FileProtocolHandler', ui.url], { windowsHide: true, stdio: 'ignore' }); browser.on('error', () => status('Could not open browser. Restart after setting a default Windows browser.'));
    console.log('Windows captions window opened. Close this console or press Ctrl+C to stop.');
    await whisper.start(status);
    if (stopped) return;
    scheduler = new Scheduler((pcm, signal) => whisper.infer(pcm, signal), event => ui.publish(event), status);
    segmenter.on('segment', job => scheduler.add(job));
    capture = new Capture(helper, pcm => segmenter.push(pcm), status, () => { segmenter.reset(); scheduler.reset(); ui.publish({ type: 'reset' }); });
    await refresh(); await capture.select(selected);
  } catch (e) { await shutdown(); throw e; }
}
if (require.main === module) main().catch(e => { console.error(e.message); process.exitCode = 1; });
module.exports = { main };
