'use strict';
// Explicit integration command, intentionally outside the default mock test glob.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const net = require('node:net');
const { WebSocket } = require('ws');
const { Segmenter } = require('../../windows-captions/audio');
const { SourceScheduler } = require('../../windows-captions/sources');
const { Whisper } = require('../../windows-captions/whisper');
const { createUI } = require('../../windows-captions/server');
const { readSpeech,report } = require('./common');
async function speechE2E(runtime, output) {
  const rows=[]; let whisper,ui,scheduler,ws;
  try {
    const pcm=readSpeech(runtime.fixture);
    // An ephemeral test-only port; production settings/environment are never changed.
    const port=await new Promise(r=>{const s=net.createServer();s.listen(0,'127.0.0.1',()=>{const p=s.address().port;s.close(()=>r(p));});});
    whisper=new Whisper({exe:runtime.server,model:runtime.model},{WHISPER_PORT:String(port),WHISPER_GPU:'false',WHISPER_THREADS:'2',WHISPER_LANGUAGE:'en'});
    const startup=[];await whisper.start(message=>startup.push(message));
    rows.push({check:'Actual CPU whisper-server health',outcome:'PASS',evidence:'Real child server returned /health status ok; CI-only tiny.en model.'});
    ui=await createUI({port:0});const received=[];
    ws=new WebSocket(ui.url.replace('http:','ws:'),{origin:new URL(ui.url).origin});
    ws.on('message',m=>received.push(JSON.parse(m)));
    await new Promise((resolve,reject)=>{ws.once('open',resolve);ws.once('error',reject);});
    const source=Object.freeze({id:'licensed-speech',generation:1,name:'Public-domain speech fixture',identity:'sha256:'+require('./assets.json').fixtureSha256});
    const failures=[], jobs=[];
    scheduler=new SourceScheduler((audio,signal)=>whisper.infer(audio,signal),e=>ui.publish(e),(_s,state,message)=>{if(state==='unavailable'||message.includes('skipped'))failures.push(message);});scheduler.attach(source);
    const segmenter=new Segmenter();segmenter.on('segment',job=>{jobs.push({id:job.id,final:job.final});scheduler.add(source,{...job,capturedAt:job.captureOffsetMs});});
    // Feed frames in batches, exercising real partial/final queue coalescing without realtime sleep.
    for(let i=0;i<pcm.length;i+=640)segmenter.push(pcm.subarray(i,i+640));
    segmenter.push(Buffer.alloc(32000)); // force a real pause boundary, not a mocked final
    const deadline=Date.now()+180000;
    while((scheduler.busy || [...scheduler.sources.values()].some(s=>s.finals.length||s.partial)) && Date.now()<deadline)await new Promise(r=>setTimeout(r,50));
    assert.ok(Date.now()<deadline,'Real Whisper inference timed out');assert.deepEqual(failures,[]);
    const captionDeadline=Date.now()+3000;
    while(!received.some(e=>e.type==='caption'&&e.final)&&Date.now()<captionDeadline)await new Promise(r=>setTimeout(r,25));
    const finals=received.filter(e=>e.type==='caption'&&e.final);
    assert.ok(finals.length>0,'No finalized caption reached the real WebSocket client');
    assert.ok(jobs.some(j=>!j.final)&&jobs.some(j=>j.final),'Fixture must exercise partial and final jobs');
    assert.ok(finals.every(e=>e.source.identity===source.identity&&e.source.name===source.name),'Source attribution changed');
    const text=finals.map(e=>e.text).join(' ').toLowerCase().replace(/[^a-z\s]/g,' ');
    const expected=require('./assets.json').expectedWords;const recall=expected.filter(word=>text.split(/\s+/).includes(word)).length/expected.length;
    assert.ok(recall>=.8 && text.includes('country') && text.includes('americans'),`Speech content mismatch (keyword recall ${recall}): ${text}`);
    assert.match(text,/ask\s+not\s+what\s+your\s+country/,'Expected ordered speech phrase');
    assert.equal(ui.state.history.length,finals.length);
    rows.push({check:'Licensed WAV → production segmentation → shared scheduler → real Whisper HTTP → publication/WebSocket history',outcome:'PASS',evidence:`${jobs.length} real segment jobs, ${finals.length} finalized captions, keyword recall ${recall.toFixed(2)}. No HTTP/inference mocks; no audio device involved.`});
  } catch(e){ rows.push({check:'Speech end-to-end integration',outcome:'FAIL',evidence:e.message}); }
  finally {scheduler?.close();ws?.terminate();await whisper?.close();await ui?.close();}
  return report(output,rows,{title:'Real Whisper speech pipeline (no WASAPI)'});
}
if(require.main===module){const runtimeFile=process.argv[2],output=process.argv[3]||'artifacts/audio-diagnostics/speech.json';(async()=>{try{const runtime=JSON.parse(fs.readFileSync(runtimeFile,'utf8').replace(/^\uFEFF/,''));const r=await speechE2E(runtime,output);if(r.outcome!=='PASS')process.exitCode=1;}catch(e){report(output,[{check:'Required integration inputs',outcome:'FAIL',evidence:e.message}]);process.exitCode=1;}})();}
module.exports={speechE2E};
