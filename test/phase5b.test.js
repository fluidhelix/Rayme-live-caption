'use strict';
const {test}=require('node:test'),assert=require('node:assert/strict');
const {CaptureSession}=require('../windows-captions/capture-session');
const {SourceManager,SourceScheduler}=require('../windows-captions/sources');
const {Segmenter}=require('../windows-captions/audio');
const {createUI}=require('../windows-captions/server');
const tick=()=>new Promise(r=>setImmediate(r));
const A={kind:'process',id:'10:100',pid:10,started:'100',name:'App A'},B={kind:'process',id:'20:200',pid:20,started:'200',name:'App B'};
function rig(publish=()=>{}){const workers=[];const scheduler=new SourceScheduler(async()=>'',publish,()=>{});const make=()=>new SourceManager({scheduler,publish,createCapture:(audio,status,reset)=>{const w={audio,closed:false,async select(d){this.descriptor=d;reset();status('Ready','capturing');},async close(){this.closed=true;}};workers.push(w);return w;}});return{workers,scheduler,make};}
function tone(ms,value=1000){const b=Buffer.alloc(ms*32);for(let i=0;i<b.length;i+=2)b.writeInt16LE(value,i);return b;}
test('startup intent waits for model readiness, selects Windows default once, and never returns after explicit application selection',async()=>{
 const r=rig(),s=new CaptureSession();try{assert.equal(r.workers.length,0);const first=r.make();await s.ready(first);await s.ready(first);assert.equal(r.workers.length,1);assert.equal(r.workers[0].descriptor,'default');await s.configure('a',A);assert.equal(r.workers[0].closed,true);assert.deepEqual([...s.manager.slots.keys()],['a']);await s.configure('b',B);assert.equal(r.workers.filter(w=>!w.closed).length,2);
 await s.suspend();assert.ok(r.workers.every(w=>w.closed));await s.ready(r.make());assert.deepEqual([...s.manager.slots.keys()],['a','b']);assert.equal(r.workers.filter(w=>!w.closed).length,2);await s.configure('a',null);await s.configure('b',null);await s.suspend();await s.ready(r.make());assert.equal(s.manager.slots.size,0);
 }finally{await s.manager?.close();r.scheduler.close();}
});
test('paused source selections and same-identity history survive processing restart without starting duplicate workers',async()=>{
 const ui=await createUI({port:0}),r=rig(ui.publish),s=new CaptureSession();try{await s.ready(r.make());const source=s.manager.slots.get('device').source;ui.publish({type:'caption',source,id:1,revision:1,final:true,text:'Remember this.',capturedAt:1});await s.pause(true);await s.suspend();const count=r.workers.length;await s.ready(r.make());assert.equal(r.workers.length,count);assert.equal(s.manager.paused,true);assert.equal(ui.state.history[0].text,'Remember this.');await s.pause(false);assert.equal(r.workers.filter(w=>!w.closed).length,1);assert.equal(ui.state.history.length,1);
 const resumed=s.manager.slots.get('device').source;assert.ok(resumed.generation>source.generation);ui.publish({type:'caption',source,id:2,revision:2,final:false,text:'obsolete'});assert.equal(ui.state.partials.device,undefined);
 }finally{await s.manager?.close();r.scheduler.close();await ui.close();}
});
test('early partials are actual growing audio windows at 700ms with unchanged final speech eligibility',()=>{
 const s=new Segmenter(),events=[];s.on('segment',j=>events.push(j));s.push(tone(2100));assert.deepEqual(events.map(e=>e.pcm.length/32),[700,1400,2100]);assert.ok(events.every(e=>!e.final));s.push(tone(900,0));assert.equal(events.at(-1).final,true);assert.equal(events.at(-1).pcm.length/32,3000);const brief=new Segmenter();let finals=0,retired=0;brief.on('segment',j=>finals+=j.final?1:0);brief.on('retire',()=>retired++);brief.push(tone(100));brief.push(tone(900,0));assert.equal(finals,0);assert.equal(retired,1);
});
test('slow inference adapts pacing and coalesces partials while two-source finals have priority',async()=>{
 let now=0;const calls=[],events=[];const q=new SourceScheduler(()=>new Promise(resolve=>calls.push(resolve)),e=>events.push(e),()=>{},()=>now);const a={id:'a',identity:'a',generation:1},b={id:'b',identity:'b',generation:2};q.attach(a);q.attach(b);
 try{const job=(id,final=false)=>({id,final,pcm:Buffer.alloc(640),audioEndAt:now});assert.equal(q.partialInterval(),700);q.add(a,job(1));for(let i=0;i<20;i++)q.add(a,job(1));q.add(b,job(1,true));q.add(a,job(1,true));assert.equal(calls.length,1);assert.equal(q.sources.get('a').partial,null);now=1800;assert.equal(q.partialInterval(),2500);calls.shift()('obsolete');await tick();assert.equal(events.filter(e=>e.type==='caption').length,0);calls.shift()('B final');await tick();calls.shift()('A final');await tick();assert.deepEqual(events.filter(e=>e.type==='caption').map(e=>e.source.id),['b','a']);assert.ok(q.partialInterval()>700);
 }finally{q.close();}
});
test('audio levels are bounded aggregate metadata, separate from failure health and stale generations',async()=>{
 const ui=await createUI({port:0}),r=rig(ui.publish);const m=r.make();try{await m.configure('a',A);const source=m.slots.get('a').source;ui.publish({type:'source-status',source,component:'inference',state:'unavailable',message:'Inference failed.'});r.workers[0].audio(tone(500,20));assert.deepEqual(Object.keys(ui.state.audioLevels.a).sort(),['dbfs','low','rms','source','type']);assert.equal(ui.state.audioLevels.a.low,true);assert.equal(ui.state.sources.a.state,'unavailable');await m.pause(true);assert.equal(ui.state.audioLevels.a,undefined);r.workers[0].audio(tone(500));assert.equal(ui.state.audioLevels.a,undefined);
 }finally{await m.close();r.scheduler.close();await ui.close();}
});
test('model readiness gates automatic capture and failed startup cannot create a worker',async()=>{
 const {SetupManager}=require('../windows-captions/setup'),fs=require('node:fs'),os=require('node:os'),path=require('node:path');
 const root=fs.mkdtempSync(path.join(os.tmpdir(),'Rayme readiness ')),file=path.join(root,'model.bin'),bytes=Buffer.alloc(1024);bytes.writeUInt32LE(0x67676d6c);fs.writeFileSync(file,bytes);const r=rig(),session=new CaptureSession();let release;
 const setup=new SetupManager({root,exe:'runtime',publish:()=>{},runtimeCheck:async()=>{},startModel:async()=>{await new Promise(resolve=>release=resolve);await session.ready(r.make());}});
 try{await setup.initialize();assert.equal(r.workers.length,0);const pending=setup.select(file);while(!release)await tick();assert.equal(setup.state.phase,'loading');assert.equal(r.workers.length,0);release();await pending;assert.equal(r.workers.length,1);await session.suspend();setup.startModel=async()=>{throw Error('Native startup failed');};await setup.setMode('cuda');assert.equal(setup.state.phase,'error');assert.ok(r.workers.every(w=>w.closed));assert.equal(setup.state.activeModel,null);
 }finally{await session.manager?.close();r.scheduler.close();fs.rmSync(root,{recursive:true,force:true});}
});
test('shutdown during source replacement cannot start a late capture worker',async()=>{
 const r=rig(),m=r.make();try{await m.configure('a',A);let release;r.workers[0].close=()=>new Promise(resolve=>release=()=>{r.workers[0].closed=true;resolve();});const replacing=m.configure('a',{...A,id:'11:101',pid:11,started:'101'});while(!release)await tick();await m.close();release();await replacing;assert.equal(r.workers.length,1);assert.equal(r.workers[0].closed,true);assert.equal(m.slots.size,0);
 }finally{r.scheduler.close();}
});
