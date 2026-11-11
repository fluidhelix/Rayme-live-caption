'use strict';
const fs = require('node:fs');
const path = require('node:path');
const { spawn } = require('node:child_process');
const { stopChild } = require('./whisper');

// One owned child per backend session. The token never enters argv, environment or logs.
function launchOverlay({ url, publish, exe = path.join(__dirname, '..', 'native', 'bin', 'CaptionOverlay.exe'), spawnChild = spawn, exists = fs.existsSync }) {
  let child, closing = false, fallbackOpened = false;
  let ready = Promise.resolve(false);
  const fallback = message => {
    if (closing || fallbackOpened) return;
    fallbackOpened = true; publish({ type: 'status', message });
    console.error(message);
    const browser = spawnChild('rundll32.exe', ['url.dll,FileProtocolHandler', url], { windowsHide: true, stdio: 'ignore' });
    browser.on('error', () => { const message = 'Browser fallback could not open. Install CaptionOverlay.exe in native\\bin and restart Windows captions.'; publish({ type: 'status', message }); console.error(message); });
  };
  if (!exists(exe)) fallback('Native overlay missing. Opening browser fallback. Extract windows-caption-overlay-win-x64 into native\\bin.');
  else {
    child = spawnChild(exe, [], { windowsHide: true, stdio: ['pipe', 'pipe', 'ignore'] });
    ready = new Promise(resolve => {
      let text='';const finish=value=>{clearTimeout(timer);resolve(value);};
      const timer=setTimeout(()=>finish(false),30000);
      child.stdout.on('data',chunk=>{text=(text+chunk.toString()).slice(-256);if(text.includes('{"type":"overlay-ready"}'))finish(true);});
      child.once('exit',()=>finish(false));child.once('error',()=>finish(false));
    });
    child.on('error', () => fallback('Native overlay could not start. Opening browser fallback.'));
    child.on('exit', () => fallback('Native overlay closed. Caption backend remains running; opening browser fallback.'));
    child.stdin.on('error', () => fallback('Native overlay session handoff failed. Opening browser fallback.'));
    child.stdin.write(JSON.stringify({ url }) + '\n'); // Keep open: backend death yields EOF in the overlay.
    ready.then(ok => { if (!ok) fallback('Native overlay did not become ready. Opening browser fallback.'); });
  }
  return { child, ready, close: async () => {
    closing = true;
    if (!child || child.exitCode !== null || child.signalCode) return;
    const exited = new Promise(resolve => { const timer = setTimeout(resolve, 1500); child.once('exit', () => { clearTimeout(timer); resolve(); }); });
    child.stdin.end(); // Give WPF time to save placement and close on EOF before forcing cleanup.
    await exited; await stopChild(child);
  } };
}
module.exports = { launchOverlay };
