'use strict';
const http = require('node:http');
const fs = require('node:fs');
const path = require('node:path');
const { randomBytes } = require('node:crypto');
const { SegmentLedger, sourceHealth } = require('./caption-lifecycle');
const historyPolicy = require('./history-policy');
const { WebSocketServer, WebSocket } = require('ws');
async function createUI({ port = 8090, onSelect, onRefresh, onConfigure, onPause, onSetup }) {
  const token = randomBytes(24).toString('hex');
  const state = { type: 'snapshot', status: 'Starting local speech recognition…', history: [], partial: null, devices: [], apps: [], sources: {}, partials: {}, paused: false, segments: {}, epochs: {}, audioLevels: {} };
  const ledgers=new Map();
  function ledger(slot,generation=0){let entry=ledgers.get(slot);if(!entry||entry.generation!==generation){entry={generation,value:new SegmentLedger()};ledgers.set(slot,entry);}return entry.value;}
  function syncLedger(slot,generation){state.segments[slot]={generation,...ledger(slot,generation).toJSON()};}
  const server = http.createServer((req, res) => {
    const url = new URL(req.url, 'http://localhost');
    if (url.pathname !== '/' || url.searchParams.get('token') !== token) { res.writeHead(404).end(); return; }
    res.writeHead(200, { 'Content-Type': 'text/html; charset=utf-8', 'Cache-Control': 'no-store', 'Content-Security-Policy': "default-src 'self'; script-src 'unsafe-inline'; style-src 'unsafe-inline'; connect-src 'self'; base-uri 'none'; frame-ancestors 'none'" });
    res.end(fs.readFileSync(path.join(__dirname, 'overlay.html'),'utf8').replace('/*RAYME_HISTORY_POLICY*/',()=>fs.readFileSync(path.join(__dirname,'history-policy.js'),'utf8')));
  });
  const wss = new WebSocketServer({ noServer: true, maxPayload: 8192 });
  server.on('upgrade', (req, socket, head) => {
    const url = new URL(req.url, 'http://localhost');
    const origin = `http://127.0.0.1:${server.address().port}`;
    if (req.headers.origin !== origin || url.searchParams.get('token') !== token) { socket.destroy(); return; }
    wss.handleUpgrade(req, socket, head, ws => wss.emit('connection', ws));
  });
  function publish(event) {
    const source=event.source,slot=source?.id||'legacy';
    if(source){
      const epoch=state.epochs[slot];
      if(epoch&&(epoch.source.generation>source.generation||epoch.source.generation===source.generation&&epoch.source.identity!==source.identity||epoch.removed&&epoch.source.generation>=source.generation))return;
      if(!epoch||epoch.source.generation<source.generation){
        if(epoch?.source.identity===source.identity&&state.partials[slot])state.history=historyPolicy.replace(state.history,{...state.partials[slot],final:false,unfinished:true});
        delete state.partials[slot];delete state.audioLevels[slot];ledgers.delete(slot);delete state.segments[slot];
        if(epoch&&epoch.source.identity!==source.identity){state.history=state.history.filter(c=>c.source?.id!==slot);delete state.sources[slot];}
        state.sources[slot]={...(state.sources[slot]||{state:'waiting',message:'Waiting for audio'}),type:'source-status',source};
      }
      state.epochs[slot]={source,removed:event.type==='source-removed'};
    }
    if(event.type==='source-status')event=sourceHealth(state.sources[slot],event);
    if (event.type === 'applications') { state.apps = event.apps; state.processSupported = event.supported; }
    if (event.type === 'source-status') state.sources[source.id] = event;
    if (event.type === 'source-removed') { delete state.audioLevels[slot]; delete state.sources[slot]; delete state.partials[slot];state.history=state.history.filter(c=>c.source?.id!==slot);ledgers.delete(slot);delete state.segments[slot]; }
    if(event.type==='audio-level'){if(!Number.isFinite(event.rms)||!Number.isFinite(event.dbfs))return;event={...event,rms:Math.max(0,Math.min(32768,event.rms)),dbfs:Math.max(-91,Math.min(0,event.dbfs))};state.audioLevels[slot]=event;}
    if (event.type === 'paused') state.paused = event.paused;
    if (event.type === 'cleared') { state.history = []; state.partials = {}; state.partial = null; }
    if (event.type === 'status') state.status = event.message;
    if (event.type === 'setup') state.setup = event;
    if (event.type === 'processing') state.processing = event;
    if (event.type === 'devices') { state.devices = event.devices; state.selected = event.selected; }
    if(event.type==='reset'){
      delete state.audioLevels[slot];
      if(source){
        const old=state.sources[slot];
        state.sources[slot]={...(old||{state:'waiting',message:'Waiting for audio'}),type:'source-status',source};
        event={...event,state:state.sources[slot].state,message:state.sources[slot].message};const pending=state.partials[slot];if(pending)ledger(slot,source.generation).mark(pending.id,pending.revision,{closed:true,cleared:true});delete state.partials[slot];syncLedger(slot,source.generation);
      }else{if(state.partial)ledger('legacy').mark(state.partial.id,state.partial.revision,{closed:true,cleared:true});state.partial=null;syncLedger('legacy',0);}
    }
    if(event.type==='segment-retired'){
      const book=ledger(slot,source?.generation||0),revision=book.revision(event.id,event.revision),terminal=event.terminal!==false;
      book.mark(event.id,revision,{closed:terminal,cleared:true});event={...event,revision,terminal};
      const pending=source?state.partials[slot]:state.partial;
      if(event.retain===true&&pending&&(pending.id<event.id||pending.id===event.id&&(terminal||(pending.revision||0)<=revision)))state.history=historyPolicy.replace(state.history,{...pending,final:false,unfinished:true});
      if(terminal&&event.retain!==true)state.history=state.history.filter(c=>!c.unfinished||historyPolicy.key(c)!==historyPolicy.key(event));
      if(pending&&(pending.id<event.id||pending.id===event.id&&(terminal||(pending.revision||0)<=revision))){if(source)delete state.partials[slot];else state.partial=null;}
      syncLedger(slot,source?.generation||0);
    }
    if(event.type==='caption'){
      const book=ledger(slot,source?.generation||0),revision=book.revision(event.id,event.revision);
      if(!book.allows(event.id,revision,event.final))return;
      event={...event,revision,text:typeof event.text==='string'?historyPolicy.text(event.text):''};
      // An empty result retires this revision; a final result closes its segment.
      if(!event.text?.trim()){
        event={type:'segment-retired',source,id:event.id,revision,terminal:!!event.final,retain:true,reason:'No recognizable speech.'};
        book.mark(event.id,revision,{closed:event.terminal,cleared:true});
        const pending=source?state.partials[slot]:state.partial;
        if(pending&&(pending.id<event.id||pending.id===event.id&&(event.terminal||(pending.revision||0)<=revision)))state.history=historyPolicy.replace(state.history,{...pending,final:false,unfinished:true});
        if(pending&&(pending.id<event.id||pending.id===event.id&&(event.terminal||(pending.revision||0)<=revision))){if(source)delete state.partials[slot];else state.partial=null;}
      }else{
        const previous=source?state.partials[slot]:state.partial;
        if(previous&&previous.id<event.id)state.history=historyPolicy.replace(state.history,{...previous,final:false,unfinished:true});
        book.mark(event.id,revision,{closed:!!event.final,cleared:!!event.final,final:!!event.final});
        if(event.final){
          if(source){if(state.partials[slot]?.id<=event.id)delete state.partials[slot];}else if(state.partial?.id<=event.id)state.partial=null;
          state.history=historyPolicy.replace(state.history,event);
        }else if(source){if(!state.partials[slot]||state.partials[slot].id<=event.id)state.partials[slot]=event;}
        else if(!state.partial||state.partial.id<=event.id)state.partial=event;
        if(!event.final)state.history=state.history.filter(c=>historyPolicy.key(c)!==historyPolicy.key(event));
      }
      syncLedger(slot,source?.generation||0);
    }
    if(source&&event.type!=='source-removed')syncLedger(slot,source.generation);
    for (const ws of wss.clients) if (ws.readyState === WebSocket.OPEN) { if (ws.bufferedAmount > 256 * 1024) ws.terminate(); else ws.send(JSON.stringify(event)); }
  }
  wss.on('connection', ws => {
    ws.send(JSON.stringify(state));
    ws.on('error', () => {});
    ws.on('message', raw => {
      try { const msg = JSON.parse(raw);
        const act = p => Promise.resolve(p).catch(e => publish({ type: 'status', message: e.message || 'Control failed.' }));
        if (msg.type === 'clear') publish({ type: 'cleared' });
        if (msg.type === 'pause' && typeof msg.paused === 'boolean') act(onPause?.(msg.paused));
        if (msg.type === 'setup' && ['select','download','retry','mode'].includes(msg.action) && (msg.path === undefined || typeof msg.path === 'string' && msg.path.length <= 4096)) act(onSetup?.(msg));
        if (msg.type === 'configure' && ['a','b','device'].includes(msg.slot)) {
          let descriptor = null;
          if (msg.enabled === true) {
            if (msg.slot === 'device') {
              const d = state.devices.find(d => d.id === msg.id);
              if (msg.id !== 'default' && !d) return;
              descriptor = { kind: 'device', id: msg.id, endpoint: msg.id, name: d?.name || 'Whole playback output' };
            } else { descriptor = state.apps.find(a => a.id === msg.id); if (!state.processSupported || !descriptor) return; }
          }
          act(onConfigure?.(msg.slot, descriptor));
        }
 if (msg.type === 'select' && (msg.id === 'default' || state.devices.some(d => d.id === msg.id))) Promise.resolve(onSelect(msg.id)).catch(() => publish({ type: 'status', message: 'Could not select audio device.' }));
        if (msg.type === 'refresh') Promise.resolve(onRefresh()).catch(() => publish({ type: 'status', message: 'Could not enumerate audio devices.' }));
      } catch {}
    });
  });
  await new Promise((resolve, reject) => { server.once('error', reject); server.listen(port, '127.0.0.1', resolve); });
  return { state, publish, url: `http://127.0.0.1:${server.address().port}/?token=${token}`, close: async () => { for (const ws of wss.clients) ws.terminate(); wss.close(); await new Promise(r => server.close(r)); } };
}
module.exports = { createUI };
