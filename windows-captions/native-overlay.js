'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { randomBytes } = require('node:crypto');
const { stopChild } = require('./whisper');

// Only the owned WPF process receives the per-launch intent capability through stdin.
// Exit code 0 alone is never treated as a user's request, and no exit opens a browser.
function launchOverlay({ url, publish, onQuit = () => {}, onFailure = () => {}, exe = path.join(__dirname, '..', 'native', 'bin', 'CaptionOverlay.exe'), spawnChild = spawn, exists = fs.existsSync }) {
  let child, closing = false, reported = false, intentional = false, finishReady;
  const capability = randomBytes(24).toString('hex');
  const ready = new Promise(resolve => { finishReady = resolve; });
  const fail = message => {
    if (closing || reported || intentional) return;
    reported = true; finishReady(false); publish({ type: 'status', message }); onFailure(new Error(message));
  };
  if (!exists(exe)) queueMicrotask(() => fail('The Rayme caption window is missing. Extract the complete Rayme ZIP again.'));
  else {
    child = spawnChild(exe, [], { windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'] });
    const timer = setTimeout(() => fail('The caption window did not finish starting. Please retry Rayme.'), 30000);
    ready.then(() => clearTimeout(timer));
    let text = '';
    child.stdout.on('data', chunk => {
      text += chunk.toString();
      if (text.length > 4096) { text = ''; fail('Invalid caption window control output.'); return; }
      let at;
      while ((at = text.indexOf('\n')) >= 0) {
        const line = text.slice(0, at); text = text.slice(at + 1);
        try {
          const event = JSON.parse(line);
          if (event.type === 'overlay-ready') finishReady(true);
          if (event.type === 'quit-rayme' && event.capability === capability && !closing && !intentional) {
            intentional = true; onQuit();
          }
        } catch { fail('Invalid caption window control message.'); }
      }
    });
    child.on('error', () => fail('The Rayme caption window could not start. Re-extract the complete package and retry.'));
    child.on('exit', () => { finishReady(false); fail('The caption window stopped unexpectedly. Rayme has stopped capture; choose Retry or Quit in the launcher.'); });
    child.stdin.on('error', () => fail('The private caption session disconnected unexpectedly.'));
    child.stdin.write(JSON.stringify({ url, capability }) + '\n');
  }
  return { child, ready, get intentional() { return intentional; }, close: async () => {
    closing = true; finishReady(false);
    if (!child || child.exitCode !== null || child.signalCode) return;
    const exited = new Promise(resolve => { const timer = setTimeout(resolve, 2000); child.once('exit', () => { clearTimeout(timer); resolve(); }); });
    child.stdin.end(JSON.stringify({ type: 'backend-closing' }) + '\n');
    await exited; await stopChild(child);
  } };
}
module.exports = { launchOverlay };
