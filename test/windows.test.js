'use strict';
const { test } = require('node:test');
const assert = require('node:assert/strict');
const http = require('node:http');
const { spawn } = require('node:child_process');
const fs = require('node:fs');
const { EventEmitter } = require('node:events');
const { PassThrough } = require('node:stream');
const { WebSocket } = require('ws');
const { Segmenter, Scheduler, wav } = require('../windows-captions/audio');
const { Whisper, waitReady, stopChild, resolveRuntime } = require('../windows-captions/whisper');
const { Capture } = require('../windows-captions/capture');
const { createUI } = require('../windows-captions/server');
const tick = () => new Promise(r => setImmediate(r));
function tone(ms, value = 1000) { const b = Buffer.alloc(ms * 32); for (let i = 0; i < b.length; i += 2) b.writeInt16LE(value, i); return b; }
async function serve(handler) { const s = http.createServer(handler); await new Promise(r => s.listen(0, '127.0.0.1', r)); return { url: `http://127.0.0.1:${s.address().port}`, close: () => new Promise(r => { s.closeAllConnections(); s.close(r); }) }; }
test('PCM WAV header and incomplete samples', () => { const b = wav(tone(100)); assert.equal(b.readUInt32LE(24), 16000); assert.equal(b.readUInt16LE(22), 1); assert.equal(b.readUInt32LE(40), 3200); assert.throws(() => wav(Buffer.alloc(1))); });
test('silence, chunk alignment, short reactions and bounded continuous audio', () => {
 const s = new Segmenter(); const jobs = []; s.on('segment', j => jobs.push(j));
 s.push(tone(60000, 0)); assert.equal(jobs.length, 0); assert.ok(s.pre.length <= 12);
 const input = Buffer.concat([tone(300), tone(1000, 0)]); for(let i=0;i<input.length;i+=137) s.push(input.subarray(i,i+137));
 assert.equal(jobs.filter(j=>j.final).length, 1); assert.ok(jobs[0].pcm.length >= 300*32);
 jobs.length=0;s.push(tone(60000));assert.equal(jobs.filter(j=>j.final).length,4);assert.ok(jobs.every(j=>j.pcm.length<=15000*32));assert.ok(s.tail.length<640);
});
test('scheduler suppresses stale partials, orders finals and bounds overload', async () => {
 const waits=[],events=[],status=[]; const q=new Scheduler(()=>new Promise(r=>waits.push(r)),e=>events.push(e),s=>status.push(s));
 q.add({id:1,pcm:tone(100),final:false});q.add({id:1,pcm:tone(200),final:true});
 waits.shift()('old');await tick();assert.equal(events.length,0);waits.shift()('*laughter*');await tick();assert.equal(events[0].text,'*laughter*');assert.equal(events[0].final,true);
 for(let id=2;id<20;id++)q.add({id,pcm:tone(100),final:true});assert.equal(q.finals.length,2);assert.ok(status.length);q.close();waits.shift()('late');await tick();assert.equal(events.length,1);
});
test('inference errors remain status, reset cancels stale device audio',async()=>{
 const events=[],states=[];let resolve;const q=new Scheduler(()=>new Promise(r=>resolve=r),e=>events.push(e),s=>states.push(s));q.add({id:1,pcm:tone(10),final:false});q.reset();resolve('stale');await tick();assert.equal(events.length,0);
 q.infer=async()=>{throw Error('server error');};q.add({id:2,pcm:tone(10),final:true});await tick();assert.equal(events.length,0);assert.equal(states.length,1);q.close();
});
test('HTTP readiness waits for health and detects startup failure/timeout',async()=>{
 let ready=false;const s=await serve((req,res)=>{res.writeHead(ready?200:503,{'Content-Type':'application/json'});res.end(JSON.stringify({status:ready?'ok':'loading'}));});
 try {await assert.rejects(waitReady(s.url,()=>false),/exited/);await assert.rejects(waitReady(s.url,()=>true,{timeout:30,interval:5}),/timed out/);ready=true;await waitReady(s.url,()=>true,{timeout:100});}finally{await s.close();}
});
test('Whisper sends local WAV, preserves annotations, rejects HTTP errors and non-JSON', async()=>{
 let mode='ok';const s=await serve(async(req,res)=>{let body=Buffer.alloc(0);for await(const c of req) body=Buffer.concat([body,c]);assert.ok(body.includes(Buffer.from('RIFF')));if(mode==='error'){res.writeHead(500);res.end('error text');}else if(mode==='html')res.end('<html>error</html>');else res.end(JSON.stringify({text:'[laughter] *coughing* *sighing*'}));});
 const w=new Whisper({});w.url=s.url;
 try {assert.equal(await w.infer(tone(100)),'[laughter] *coughing* *sighing*');mode='error';await assert.rejects(w.infer(tone(100)),/HTTP 500/);mode='html';await assert.rejects(w.infer(tone(100)));}finally{await s.close();}
});
test('missing runtime is actionable without needing Discord credentials',()=>{assert.throws(()=>resolveRuntime({},'/does-not-exist'),/Missing local Whisper/);});
test('test-only startup diagnostics report a real spawn failure without environment secrets',async()=>{
 const s=await serve((_req,res)=>res.end());const port=new URL(s.url).port;await s.close();
 let diagnostic;
 const w=new Whisper({exe:require('node:path').join(process.cwd(),'missing-whisper-runtime','whisper-server.exe'),model:'unused.bin'},
  {WHISPER_PORT:port,WHISPER_THREADS:'secret value',PRIVATE_TOKEN:'must-not-appear'},
  {testOnlyStartupDiagnostics:d=>{diagnostic=d;}});
 try {
  await assert.rejects(w.start(()=>{}),/exited before becoming ready/);
  assert.equal(diagnostic.spawnError,'ENOENT');assert.equal(diagnostic.exitCode,w.child.exitCode);
  assert.equal(diagnostic.stdout,'');assert.equal(diagnostic.stderr,'');
  assert.equal(diagnostic.args.includes('-nc'),false);assert.ok(diagnostic.args.includes('[redacted]'));
  assert.ok(!JSON.stringify(diagnostic).includes('must-not-appear'));
 } finally {await w.close();}
});
test('capture disconnect resets pipeline; close kills owned child and cancels retry',async()=>{
 let child;const status=[];let resets=0;let bytes=0;
 const c=new Capture('fake',b=>bytes+=b.length,s=>status.push(s),()=>resets++,()=>{child=new EventEmitter();child.stdout=new PassThrough();child.stderr=new PassThrough();child.exitCode=null;child.kill=()=>{child.exitCode=0;child.emit('exit',0);};return child;});
 await c.select('default');child.stdout.write(tone(10));assert.equal(bytes,320);child.emit('exit',2);assert.ok(c.retry);assert.equal(resets,2);await c.select('another');child.stderr.write('{"type":"ready"}\n');assert.equal(status.at(-1),'Listening to playback audio');await c.close();assert.equal(c.child,null);assert.equal(c.retry,null);
});
test('child shutdown exits a real spawned process',async()=>{const c=spawn(process.execPath,['-e','setInterval(()=>{},1000)']);await new Promise(r=>c.once('spawn',r));await stopChild(c);assert.ok(c.exitCode!==null||c.signalCode);});
test('UI rejects unauthorized connections, replays 30 history entries and latest partial on reconnect',async()=>{
 const ui=await createUI({port:0,onSelect:async()=>{},onRefresh:async()=>{}});
 try{
  assert.equal((await fetch(ui.url.split('?')[0])).status,404);
  assert.equal((await fetch(ui.url)).status,200);
  for(let id=1;id<=35;id++)ui.publish({type:'caption',id,text:'sentence '+id,final:true});ui.publish({type:'caption',id:36,text:'partial',final:false});
  for(let i=0;i<2;i++){const ws=new WebSocket(ui.url.replace('http:','ws:'),{origin:new URL(ui.url).origin});const data=await new Promise((r,j)=>{ws.once('message',m=>r(JSON.parse(m)));ws.once('error',j);});assert.equal(data.history.length,30);assert.equal(data.history[0].id,6);assert.equal(data.partial.id,36);ws.terminate();}
  await new Promise(r=>{const bad=new WebSocket(ui.url.replace('http:','ws:'),{origin:'https://evil.example'});bad.once('error',()=>r());});
 }finally{await ui.close();}
});
test('Discord entry, overlay and launcher remain unchanged (allowing checkout line endings)',()=>{
 const hashes={"index.js": "018aa3eb67ce0bdff2f6c7183236c84b4884eae83a41928624c7878055d49df5", "indexold.js": "98372b197a0878c8c28e11315e4e1bd4f97a1be8788e30fd563d5385fad4917e", "overlay.html": "8ab6bc2d7ece287f88c180d18caa4221d5cdd404648b5f6282bfd6cad7c11d85", "START CAPTIONS.bat": "89ab4c76609fa8f7df8a458e376b42bfdcda883a1ffd2cf4353823dd84094317"};for(const [file,hash] of Object.entries(hashes))assert.equal(require('node:crypto').createHash('sha256').update(fs.readFileSync(file,'utf8').replace(/\r\n/g,'\n')).digest('hex'),hash,file);
 assert.equal(require('../package.json').scripts.start,'node index.js');assert.ok(!fs.readFileSync('windows-captions/main.js','utf8').includes("require('discord"));
});
