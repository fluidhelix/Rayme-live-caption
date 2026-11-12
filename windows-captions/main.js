'use strict';
if (!process.env.RAYME_PORTABLE) require('dotenv').config();
const path = require('node:path');
const fs = require('node:fs');
const { launchOverlay } = require('./native-overlay');
const { SourceScheduler, SourceManager } = require('./sources');
const { Whisper, resolveRuntime } = require('./whisper');
const { listDevices, listApplications } = require('./capture');
const { createUI } = require('./server');
const { SetupManager } = require('./setup');
const { ProcessingSession } = require('./processing');
const { CaptureSession } = require('./capture-session');
async function main() {
  if (process.platform !== 'win32') throw new Error('Rayme requires Windows 11 x64.');
  const portable = process.env.RAYME_PORTABLE === '1';
  const appRoot = portable ? path.resolve(process.env.RAYME_APP_ROOT) : path.join(__dirname, '..');
  const helper = portable ? path.join(appRoot, 'native', 'LoopbackCapture.exe') : path.join(appRoot, 'native', 'bin', 'LoopbackCapture.exe');
  let ui, overlay, whisper, manager, scheduler, setup, stopped = false, controls = Promise.resolve(), shutdownTask;
  const captureSession=new CaptureSession();
  const notify = event => { if (portable) process.stdout.write(JSON.stringify(event) + '\n'); };
  const control = task => { controls = controls.catch(() => {}).then(() => { if (!stopped) return task(); }); return controls; };
  const shutdown = (reason = 'user-quit') => {
    if (shutdownTask) return shutdownTask;
    notify({ type: 'shutdown-started', reason });
    stopped = true; setup?.cancel(); scheduler?.close();
    shutdownTask = (async () => {
      await Promise.allSettled([manager?.close(), whisper?.close()]);
      await controls.catch(() => {});
      await overlay?.close(); await ui?.close();
      process.stdin.pause();
      notify({ type: 'session-ended', reason });
    })();
    return shutdownTask;
  };
  const quit = () => shutdown('user-quit').then(() => process.exit(0));
  const fail = error => {
    if (stopped) return;
    ui?.publish({ type: 'status', message: error.message });
    notify({ type: 'failure', message: error.message });
    shutdown('failure').then(() => process.exit(2));
  };
  process.once('SIGINT', quit); process.once('SIGTERM', quit);
  if (portable) {
    let input = '';
    process.stdin.setEncoding('utf8');process.stdin.on('data', bytes => {
      input += bytes;if (input.length > 4096) { fail(Error('The launcher control pipe sent an invalid message.')); return; }
      let at;while ((at = input.indexOf('\n')) >= 0) { const line = input.slice(0,at);input = input.slice(at+1);try { if (JSON.parse(line).type === 'quit') quit(); } catch {} }
    });
    process.stdin.once('end', () => shutdown('parent-closed').then(() => process.exit(0)));process.stdin.resume();
  }
  try {
    const threshold = Number(process.env.WINDOWS_SILENCE_RMS || 80);
    const captionMaxAgeMs=Number(process.env.CAPTION_MAX_AGE_MS||15000);
    if(!Number.isFinite(captionMaxAgeMs)||captionMaxAgeMs<1000||captionMaxAgeMs>120000)throw Error('Caption freshness must be between 1000 and 120000 ms.');
    if (!Number.isFinite(threshold) || threshold < 0 || threshold > 32767) throw new Error('The audio sensitivity setting is invalid.');
    const refresh = async () => {
      ui.publish({ type: 'devices', devices: await listDevices(helper), selected: process.env.WINDOWS_AUDIO_DEVICE || 'default' });
      try { ui.publish({ type: 'applications', ...await listApplications(helper) }); }
      catch { ui.publish({ type: 'status', message: 'Application capture is unavailable. Whole-device fallback remains available.' }); }
    };
    const initializeModel = async (model, exe,mode='auto') => {
      scheduler?.close();await captureSession.suspend();manager=null;await whisper?.close();
      if (stopped) return;
      const runtime = { exe, model };
      whisper = portable?new ProcessingSession({root:path.join(appRoot,'whisper'),publish:ui.publish,onFailure:fail}):new Whisper(runtime,process.env);
      try { if(portable)await whisper.start(model,mode);else await whisper.start(message=>ui.publish({type:'status',message})); }
      catch(error) { await whisper.close();throw Error('The speech model could not load. Choose a compatible ggml model or another processing mode. '+error.message); }
      if (stopped) return;
      scheduler = new SourceScheduler((pcm, signal) => whisper.infer(pcm, signal), event => ui.publish(event), (source, state, message, health) => ui.publish({ type: 'source-status', source, state, message, ...health }), Date.now, {maxAgeMs:captionMaxAgeMs});
      manager = new SourceManager({ helper, scheduler, publish: event => ui.publish(event), threshold });
      await refresh();
      if(stopped){await manager.close();return;}
      await captureSession.ready(manager);
      if(stopped){await manager.close();return;}
      ui.publish({type:'paused',paused:captureSession.paused});
      ui.publish({ type: 'status', message: captureSession.paused?'Speech recognition ready. Capture remains paused.':manager.slots.size?'Speech recognition ready. Capturing the selected audio sources locally.':'Speech recognition ready. No audio source is enabled; choose one in Controls.' });
      notify({ type: 'speech-ready' });
      return portable?whisper.state:undefined;
    };
    ui = await createUI({ port: portable ? 0 : Number(process.env.WINDOWS_CAPTION_PORT || 8090), onRefresh: () => control(refresh),
      onConfigure: (slot, descriptor) => control(async () => { if (!manager || setup?.state.required) throw Error('Complete speech model setup before enabling capture.'); await captureSession.configure(slot, descriptor); }),
      onPause: paused => control(async () => { await captureSession.pause(paused); }),
      onSetup: command => control(async () => {
        if (!setup) throw Error('Use the portable Rayme application for guided setup.');
        if(command.action==='mode'){
          if(!['auto','cuda','cpu'].includes(command.mode))throw Error('Choose Auto, NVIDIA GPU (CUDA), or CPU.');
          setup.update({required:true,activeModel:null,phase:'switching',processingMode:command.mode,message:'Switching processing mode. Stopping capture and the previous speech runtime…'});
        }
        setup.update({required:true,activeModel:null,phase:'switching',message:'Preparing speech recognition. Your audio selections will be restored when ready.'});
        scheduler?.close();await captureSession.suspend();manager=null;await whisper?.close();whisper=null;
        if (command.action === 'mode') await setup.setMode(command.mode);
        if (command.action === 'select') await setup.select(command.path);
        if (command.action === 'download') await setup.download(command.model);
        if (command.action === 'retry') await setup.initialize();
      }) });
    overlay = launchOverlay({ url: ui.url, publish: ui.publish, onQuit: quit, onFailure: fail,
      exe: portable ? path.join(appRoot, 'native', 'CaptionOverlay.exe') : path.join(appRoot, 'native', 'bin', 'CaptionOverlay.exe') });
    await overlay.ready;if (stopped) return;
    notify({ type: 'session-started' });
    if (!fs.existsSync(helper)) throw Error('The audio helper is missing. Re-extract the complete Rayme ZIP.');
    if (portable) {
      setup = new SetupManager({ root: process.env.RAYME_DATA_ROOT || path.join(process.env.LOCALAPPDATA, 'Rayme', 'Runtime'), exe: path.join(appRoot, 'whisper', 'cpu','whisper-server.exe'), publish: ui.publish, startModel: (model,mode) => initializeModel(model,undefined,mode) });
      await control(() => setup.initialize());
      if (setup.state.required) notify({ type: 'setup-required' });
    } else {
      try { const runtime = resolveRuntime();await control(() => initializeModel(runtime.model, runtime.exe)); }
      catch(error) { ui.publish({ type: 'status', message: error.message }); }
    }
  } catch (error) { fail(error); }
  return { shutdown };
}
if (require.main === module) main().catch(error => { process.stderr.write(error.message + '\n');process.exitCode = 1; });
module.exports = { main };
