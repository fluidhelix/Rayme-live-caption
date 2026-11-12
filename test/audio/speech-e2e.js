'use strict';
// Explicit integration command, intentionally outside the default mock test glob.
const assert = require('node:assert/strict');
const fs = require('node:fs');
const net = require('node:net');
const path = require('node:path');
const { spawnSync } = require('node:child_process');
const { WebSocket } = require('ws');
const productionRoot=process.env.RAYME_TEST_BACKEND||path.join(__dirname,'../../windows-captions');
const { Segmenter } = require(path.join(productionRoot,'audio'));
const { SourceScheduler } = require(path.join(productionRoot,'sources'));
const { Whisper } = require(path.join(productionRoot,'whisper'));
const { createUI } = require(path.join(productionRoot,'server'));
const { readSpeech,report } = require('./common');
async function speechE2E(runtime, output) {
  const rows=[]; let whisper,ui,scheduler,ws,startupDiagnostics,runtimeDiagnostics;
  try {
    const cwd=path.dirname(runtime.server);
    const probe=args=>{const result=spawnSync(runtime.server,args,{cwd,windowsHide:true,timeout:30000,maxBuffer:16384,
      env:{...process.env,PATH:cwd+path.delimiter+process.env.PATH},encoding:'utf8'});
      return {args,exitCode:result.status,signal:result.signal,error:result.error?.code,stdout:(result.stdout||'').slice(0,8192),stderr:(result.stderr||'').slice(0,8192)};};
    runtimeDiagnostics={server:runtime.server,cwd,files:fs.readdirSync(cwd).filter(f=>/\.(dll|exe)$/i.test(f)).slice(0,100),
      model:{path:runtime.model,bytes:fs.statSync(runtime.model).size},help:probe(['--help'])};
    assert.equal(runtimeDiagnostics.help.error,undefined,'Native --help failed to execute');
    assert.equal(runtimeDiagnostics.help.exitCode,0,'Native --help failed (check runtime DLLs)');
    assert.match(runtimeDiagnostics.help.stdout+runtimeDiagnostics.help.stderr,/--model/,'Native --help did not print server flags');
    // Preserve native evidence for the original launcher failure with this pinned build.
    // Only replay when help confirms -nc is unsupported, so no second server can start.
    if(!/(?:^|\s)-nc(?:\s|,)|--no-context/m.test(runtimeDiagnostics.help.stdout+runtimeDiagnostics.help.stderr)) {
      runtimeDiagnostics.originalStartup=probe(['-m',runtime.model,'--host','127.0.0.1','--port','8178','-t','2','-l','en','-bs','5','-bo','5','-nc','-ng']);
      assert.match(runtimeDiagnostics.originalStartup.stderr,/unknown argument: -nc/,'Original startup failed for an unexpected native reason');
      rows.push({check:'Original native startup diagnosis',outcome:'PASS',evidence:`Original arguments: exit ${runtimeDiagnostics.originalStartup.exitCode}; ${runtimeDiagnostics.originalStartup.stderr.split(/\r?\n/)[0]}. Actual compiled --help succeeds from the executable directory; full help, arguments, DLL inventory and model metadata are in speech.json.`});
    }
    const pcm=readSpeech(runtime.fixture);
    // An ephemeral test-only port; production settings/environment are never changed.
    const port=await new Promise(r=>{const s=net.createServer();s.listen(0,'127.0.0.1',()=>{const p=s.address().port;s.close(()=>r(p));});});
    whisper=new Whisper({exe:runtime.server,model:runtime.model},{WHISPER_PORT:String(port),WHISPER_GPU:'false',WHISPER_THREADS:'2',WHISPER_LANGUAGE:'en'},
      {testOnlyStartupDiagnostics:diagnostics=>{startupDiagnostics=diagnostics;}});
    const startup=[];await whisper.start(message=>startup.push(message));
    rows.push({check:'Actual CPU whisper-server health',outcome:'PASS',evidence:'Real child server returned /health status ok; CI-only tiny.en model.'});
    ui=await createUI({port:0});const received=[];
    ws=new WebSocket(ui.url.replace('http:','ws:'),{origin:new URL(ui.url).origin});
    ws.on('message',m=>received.push(JSON.parse(m)));
    await new Promise((resolve,reject)=>{ws.once('open',resolve);ws.once('error',reject);});
    const source=Object.freeze({id:'licensed-speech',generation:1,name:'Public-domain speech fixture',identity:'sha256:'+require('./assets.json').fixtureSha256});
    const failures=[], jobs=[],measurements=[];
    // This is an offline CPU speech/accuracy integration test, not a real-time latency test.
    // Allow slow hosted runners to finish real inference; deterministic lifecycle tests
    // independently enforce the 15-second production caption freshness policy.
    scheduler=new SourceScheduler(async(audio,signal)=>{const start=performance.now();try{return await whisper.infer(audio,signal);}finally{measurements.push({audioMs:audio.length/32,inferenceMs:Math.round(performance.now()-start)});}},e=>ui.publish(e),(_s,state,message)=>{if(state==='unavailable'||message.includes('skipped'))failures.push(message);},Date.now,{maxAgeMs:120000});scheduler.attach(source);
    const segmenter=new Segmenter();segmenter.on('segment',job=>{jobs.push({id:job.id,final:job.final});scheduler.add(source,{...job,capturedAt:job.captureOffsetMs});});
    // Feed frames in batches, exercising real partial/final queue coalescing without realtime sleep.
    for(let i=0;i<pcm.length;i+=640)segmenter.push(pcm.subarray(i,i+640));
    segmenter.push(Buffer.alloc(32000)); // force a real pause boundary, not a mocked final
    const deadline=Date.now()+180000;
    while((scheduler.busy || [...scheduler.sources.values()].some(s=>s.finals.length||s.partial)) && Date.now()<deadline)await new Promise(r=>setTimeout(r,50));
    assert.ok(Date.now()<deadline,'Real Whisper inference timed out');assert.deepEqual(failures,[]);
    assert.ok(measurements.length>=2,'Real incremental and final inference must both execute');
    assert.ok(measurements[0].audioMs>=600&&measurements[0].audioMs<=800,'First actual inference must use an early audio window');
    rows.push({check:'Measured incremental inference throughput',outcome:'PASS',evidence:JSON.stringify({attempts:measurements,adaptivePartialIntervalMs:scheduler.partialInterval(),note:'Actual CPU decode timings on this runner; not GPU or production-model latency validation. Audio is batch-fed, so this is not end-to-end real-time latency.'})});
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
  } catch(e){ rows.push({check:'Speech end-to-end integration',outcome:'FAIL',evidence:e.message+(startupDiagnostics?'\nNative startup: '+JSON.stringify(startupDiagnostics):'')}); }
  finally {scheduler?.close();ws?.terminate();await whisper?.close();await ui?.close();}
  return report(output,rows,{title:'Real Whisper speech pipeline (no WASAPI)',startupDiagnostics,runtimeDiagnostics});
}
if(require.main===module){const runtimeFile=process.argv[2],output=process.argv[3]||'artifacts/audio-diagnostics/speech.json';(async()=>{try{const runtime=JSON.parse(fs.readFileSync(runtimeFile,'utf8').replace(/^\uFEFF/,''));const r=await speechE2E(runtime,output);if(r.outcome!=='PASS')process.exitCode=1;}catch(e){report(output,[{check:'Required integration inputs',outcome:'FAIL',evidence:e.message}]);process.exitCode=1;}})();}
module.exports={speechE2E};
