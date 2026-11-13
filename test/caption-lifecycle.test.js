'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {SourceScheduler,SourceManager}=require('../windows-captions/sources');
const {Segmenter}=require('../windows-captions/audio');
const {SegmentLedger,sourceHealth}=require('../windows-captions/caption-lifecycle');
const {createUI}=require('../windows-captions/server');
const {WebSocket}=require('ws');
const tick=()=>new Promise(r=>setImmediate(r));
const A=Object.freeze({id:'a',generation:1,name:'Application A',identity:'10:100'}),B=Object.freeze({id:'b',generation:2,name:'Application B',identity:'20:200'});
class Clock{
 constructor(){this.time=100000;this.timers=new Map();this.id=0;}
 now=()=>this.time;
 set=(fn,delay)=>{const id=++this.id;this.timers.set(id,{fn,at:this.time+delay});return id;};
 clear=id=>this.timers.delete(id);
 advance(ms){const end=this.time+ms;for(;;){const next=[...this.timers].filter(([,t])=>t.at<=end).sort((a,b)=>a[1].at-b[1].at)[0];if(!next)break;this.time=next[1].at;this.timers.delete(next[0]);next[1].fn();}this.time=end;}
}
function fixture(ui,maxAgeMs=1000){
 const clock=new Clock(),events=[],states=[],calls=[];
 const publish=e=>{events.push(e);ui?.publish(e);};
 const q=new SourceScheduler((pcm,signal)=>new Promise((resolve,reject)=>calls.push({pcm,signal,resolve,reject})),publish,(source,state,message,meta)=>{states.push({source,state,message,...meta});ui?.publish({type:'source-status',source,state,message,...meta});},clock.now,{maxAgeMs,setTimer:clock.set,clearTimer:clock.clear});
 for(const source of[A,B]){q.attach(source);ui?.publish({type:'reset',source});}
 const job=(id,final=true,extra={})=>({id,final,pcm:Buffer.alloc(640),capturedAt:clock.time-15000,audioEndAt:clock.time,...extra});
 return{clock,events,states,calls,q,publish,job,captions:()=>events.filter(e=>e.type==='caption')};
}
test('fake clock: freshness is measured from audio end and stale completed inference never publishes',async()=>{
 const f=fixture();try{f.q.add(A,f.job(1));f.clock.advance(1001);f.calls.shift().resolve('too old');await tick();assert.equal(f.captions().length,0);assert.ok(f.events.some(e=>e.type==='segment-retired'&&e.id===1&&e.terminal));assert.match(f.states.at(-1).message,/skipped/);
 f.q.add(A,f.job(2));f.calls.shift().resolve('fresh long utterance');await tick();assert.equal(f.captions()[0].text,'fresh long utterance');assert.deepEqual(f.captions()[0].source,A);assert.ok(Object.isFrozen(f.captions()[0].source));assert.equal(f.captions()[0].capturedAt,f.clock.time-15000);
 }finally{f.q.close();}
});
test('fake clock: stale backlogs are pruned for both sources while fresh finals remain fair',async()=>{
 const f=fixture(undefined,2000);try{f.q.add(A,f.job(1));f.q.add(B,f.job(1));f.q.add(A,f.job(2));f.clock.advance(1000);f.q.add(B,f.job(2));f.clock.advance(1001);f.calls.shift().resolve('old active result');await tick();
 assert.equal(f.calls.length,1);assert.equal(f.q.sources.get('a').finals.length,0);assert.ok(f.events.some(e=>e.source.id==='b'&&e.type==='segment-retired'&&e.id===1));assert.ok(f.states.some(e=>e.source.id==='a'&&e.message.includes('skipped')));
 f.calls.shift().resolve('B current');await tick();f.q.add(A,f.job(3));f.q.add(B,f.job(3));f.calls.shift().resolve('A current');await tick();f.calls.shift().resolve('B next');await tick();assert.deepEqual(f.captions().map(e=>e.source.id),['b','a','b']);
 }finally{f.q.close();}
});
test('fake clock: a visible partial expires during stalled inference; a fresh later final remains valid',async()=>{
 const ui=await createUI({port:0}),f=fixture(ui);try{f.q.add(A,f.job(1,false));f.calls.shift().resolve('unfinished');await tick();assert.ok(ui.state.partials.a);
 f.q.add(B,f.job(1));f.clock.advance(1001);assert.equal(ui.state.partials.a,undefined);assert.ok(f.events.some(e=>e.type==='segment-retired'&&e.source.id==='a'&&!e.terminal));
 assert.equal(ui.state.history[0].text,'unfinished');assert.equal(ui.state.history[0].unfinished,true);assert.equal(ui.state.history[0].final,false);
 f.calls.shift().resolve('B stale');await tick();f.q.add(A,f.job(1));f.calls.shift().resolve('A complete');await tick();assert.equal(ui.state.history.length,1);assert.equal(ui.state.history[0].text,'A complete');assert.equal(ui.state.history[0].unfinished,undefined);assert.equal(f.clock.timers.size,0);
 }finally{f.q.close();await ui.close();}
});
function tone(ms,value=1000){const b=Buffer.alloc(ms*32);for(let i=0;i<b.length;i+=2)b.writeInt16LE(value,i);return b;}
test('default segment rejection retires an already visible partial and prevents late revival',async()=>{
 const ui=await createUI({port:0}),f=fixture(ui),s=new Segmenter();try{s.on('segment',j=>f.q.add(A,{...j,audioEndAt:f.clock.time}));s.on('retire',j=>f.q.retire(A,j.id,j.reason));
 for(let i=0;i<3;i++)s.push(Buffer.concat([tone(20),tone(800,0)]));s.push(Buffer.concat([tone(20),tone(20,0)]));assert.equal(f.calls.length,1);f.calls.shift().resolve('spurious short sounds');await tick();assert.ok(ui.state.partials.a);
 s.push(tone(880,0));assert.equal(ui.state.partials.a,undefined);assert.equal(ui.state.history.length,0);assert.ok(f.events.some(e=>e.type==='segment-retired'&&e.terminal));
 ui.publish({type:'caption',source:A,id:1,revision:100,final:false,text:'late rejected text'});assert.equal(ui.state.partials.a,undefined);
 }finally{f.q.close();await ui.close();}
});
test('inference failure stays visible through reset and capture readiness, until an actual retry succeeds',async()=>{
 const ui=await createUI({port:0}),f=fixture(ui);try{ui.publish({type:'source-status',source:A,component:'capture',state:'waiting',message:'Capture active',recovered:true});
 f.q.add(A,f.job(1,false));f.calls.shift().resolve('draft');await tick();f.q.add(A,f.job(1,false));f.calls.shift().reject(Error('failure'));await tick();assert.equal(ui.state.partials.a,undefined);assert.equal(ui.state.sources.a.state,'unavailable');
 ui.publish({type:'reset',source:A});ui.publish({type:'source-status',source:A,component:'capture',state:'waiting',message:'Listening',recovered:true});assert.equal(ui.state.sources.a.state,'unavailable');assert.match(ui.state.sources.a.message,/failed/);
 f.q.add(A,f.job(1));assert.equal(ui.state.sources.a.state,'recovering');assert.match(ui.state.sources.a.message,/failed/);f.calls.shift().resolve('');await tick();assert.equal(ui.state.partials.a,undefined);assert.equal(ui.state.sources.a.state,'waiting');assert.equal(ui.state.history.length,1);assert.equal(ui.state.history[0].text,'draft');assert.equal(ui.state.history[0].unfinished,true);assert.equal(ui.state.history[0].final,false);
 f.q.add(A,f.job(2,false));f.calls.shift().resolve('draft two');await tick();f.q.add(A,f.job(2,false));f.calls.shift().resolve(' ');await tick();assert.equal(ui.state.partials.a,undefined);
 f.q.add(A,f.job(2));f.calls.shift().resolve('final two');await tick();assert.equal(ui.state.history.length,2);assert.equal(ui.state.history[1].text,'final two');assert.equal(ui.state.history[1].final,true);
 }finally{f.q.close();await ui.close();}
});
test('pause/resume rejects callbacks from the old worker; removal invalidates active inference',async()=>{
 const ui=await createUI({port:0}),f=fixture(ui),workers=[];const manager=new SourceManager({scheduler:f.q,publish:ui.publish,now:f.clock.now,createCapture:(audio,status,reset)=>{const w={audio,status,reset,async select(){reset();status('Ready','capturing');},async close(){}};workers.push(w);return w;}});
 try{await manager.configure('a',{kind:'process',id:'10:100',pid:10,started:'100',name:'A'});workers[0].audio(tone(2600));assert.equal(f.calls.length,1);await manager.pause(true);await manager.pause(false);const current=manager.slots.get('a').source;
 workers[0].status('old failure','unavailable');workers[0].reset();workers[0].audio(tone(2600));assert.equal(manager.slots.get('a').source,current);assert.notEqual(ui.state.sources.a.state,'unavailable');f.calls.shift().resolve('old generation');await tick();assert.equal(f.captions().length,0);
 workers[1].audio(Buffer.concat([tone(400),tone(900,0)]));assert.equal(f.calls.length,1);await manager.remove('a');f.calls.shift().resolve('removed source');await tick();assert.equal(f.captions().length,0);assert.equal(ui.state.sources.a,undefined);
 }finally{await manager.close();f.q.close();await ui.close();}
});
test('retirements and out-of-order finals cannot erase newer partials or finalized history',async()=>{
 const ui=await createUI({port:0});try{for(const source of[A,B])ui.publish({type:'reset',source});
 const caption=(source,id,revision,final,text)=>ui.publish({type:'caption',source,id,revision,final,text,capturedAt:id});
 caption(A,1,1,false,'old');caption(A,2,2,false,'new');ui.publish({type:'segment-retired',source:A,id:1,revision:1,terminal:true});assert.equal(ui.state.partials.a.text,'new');
 ui.publish({type:'segment-retired',source:A,id:2,revision:1,terminal:false});assert.equal(ui.state.partials.a.text,'new');ui.publish({type:'segment-retired',source:A,id:2,revision:3,terminal:false});assert.equal(ui.state.partials.a,undefined);
 caption(A,1,99,false,'obsolete older segment');caption(A,2,2,false,'obsolete revision');assert.equal(ui.state.partials.a,undefined);caption(A,2,4,false,'fresh revision');assert.equal(ui.state.partials.a.text,'fresh revision');
 caption(B,1,1,true,'B final');caption(A,4,6,true,'four');caption(A,3,5,true,'three');assert.deepEqual(ui.state.history.filter(c=>c.final).map(c=>c.text),['B final','three','four']);assert.deepEqual(ui.state.history.filter(c=>c.unfinished).map(c=>c.text),['fresh revision']);caption(A,4,100,true,'duplicate');assert.equal(ui.state.history.length,4);
 ui.publish({type:'segment-retired',source:A,id:4,revision:100,terminal:true});assert.equal(ui.state.history.length,4);assert.equal(ui.state.history.filter(c=>c.final).length,3);
 ui.publish({type:'source-removed',source:A});caption(A,5,101,false,'removed');ui.publish({type:'source-status',source:A,state:'capturing',message:'old ready'});assert.equal(ui.state.sources.a,undefined);assert.equal(ui.state.partials.a,undefined);assert.equal(ui.state.history[0].text,'B final');
 }finally{await ui.close();}
});
test('reconnect snapshot includes bounded retirement/generation guards and persistent health',async()=>{
 const ui=await createUI({port:0});let ws;try{ui.publish({type:'reset',source:A});ui.publish({type:'source-status',source:A,component:'inference',state:'unavailable',message:'Local transcription failed.'});
 for(let id=1;id<=300;id++)ui.publish({type:'segment-retired',source:A,id,revision:id,terminal:true});ui.publish({type:'reset',source:A});
 ws=new WebSocket(ui.url.replace('http:','ws:'),{origin:new URL(ui.url).origin});const snapshot=await new Promise((resolve,reject)=>{ws.once('message',m=>resolve(JSON.parse(m)));ws.once('error',reject);});assert.equal(snapshot.sources.a.state,'unavailable');assert.equal(snapshot.segments.a.entries.length,128);assert.equal(snapshot.segments.a.floor,172);
 ui.publish({type:'caption',source:A,id:1,revision:1000,final:false,text:'too old'});assert.equal(ui.state.partials.a,undefined);ui.publish({type:'caption',source:{...A,identity:'wrong'},id:301,revision:1,final:true,text:'wrong identity'});assert.equal(ui.state.history.length,0);
 }finally{ws?.terminate();await ui.close();}
});
test('segment ledgers close terminal segments regardless of old revision and bound all tombstones',()=>{
 const l=new SegmentLedger();l.mark(1,5);l.mark(1,2,{closed:true,cleared:true});assert.equal(l.allows(1,6),false);for(let id=2;id<=1000;id++)l.mark(id,id,{closed:true});assert.equal(l.entries.size,128);assert.equal(l.allows(1,10000,true),false);
 assert.throws(()=>fixture(undefined,0),/freshness/);assert.throws(()=>fixture(undefined,120001),/freshness/);
});
test('fake clock: queued speech reports transcribing then falling behind without concurrent inference',async()=>{
 const ui=await createUI({port:0}),f=fixture(ui,10000);try{f.q.add(A,f.job(1));f.q.add(B,f.job(1));assert.equal(f.calls.length,1);assert.equal(ui.state.sources.b.state,'transcribing');f.clock.advance(3001);assert.equal(ui.state.sources.b.state,'delayed');assert.match(ui.state.sources.b.message,/queue/);f.calls.shift().resolve('A');await tick();assert.equal(f.calls.length,1);f.calls.shift().resolve('B');await tick();assert.deepEqual(f.captions().map(e=>e.source.id),['a','b']);assert.equal(f.clock.timers.size,0);
 }finally{f.q.close();await ui.close();}
});
test('fake clocks: wall-clock jumps cannot extend or prematurely expire an enqueued job',async()=>{
 for(const forward of[false,true]){let wall=100000,resolve;const monotonic=new Clock(),events=[];
 const q=new SourceScheduler(()=>new Promise(r=>resolve=r),e=>events.push(e),()=>{},()=>wall,{maxAgeMs:1000,elapsedNow:monotonic.now,setTimer:monotonic.set,clearTimer:monotonic.clear});q.attach(A);
 try{q.add(A,{id:1,final:true,pcm:Buffer.alloc(640),audioEndAt:wall});wall+=forward?100000:-100000;if(!forward)monotonic.advance(1001);resolve('result');await tick();assert.equal(events.some(e=>e.type==='caption'),forward);}finally{q.close();}
 }
});
test('duplicate active finals are not inferred twice; canceled work returns health to waiting',async()=>{
 const f=fixture();try{f.q.add(A,f.job(1));f.q.add(A,f.job(1));assert.equal(f.q.sources.get('a').finals.length,0);f.calls.shift().resolve('one');await tick();assert.equal(f.calls.length,0);assert.equal(f.captions().length,1);
 f.q.add(A,f.job(2,false));f.q.retire(A,2,'rejected');f.calls.shift().resolve('retired');await tick();assert.equal(f.captions().length,1);assert.equal(f.states.at(-1).state,'waiting');
 }finally{f.q.close();}
});
test('rapid pause/resume and replacement isolate worker and segmenter callbacks from every old epoch',async()=>{
 const f=fixture(),events=[],workers=[],oldSegments=[];const m=new SourceManager({scheduler:f.q,publish:e=>events.push(e),now:f.clock.now,createCapture:(audio,status,reset)=>{const w={audio,status,reset,async select(){reset();status('Ready','capturing');},async close(){}};workers.push(w);return w;}});
 try{await m.configure('a',{kind:'process',id:'10:100',pid:10,started:'100',name:'A'});for(let i=0;i<3;i++){oldSegments.push(m.slots.get('a').segmenter);await m.pause(true);await m.pause(false);}
 const source=m.slots.get('a').source,before=events.length;for(const w of workers.slice(0,-1)){w.audio(tone(2600));w.status('old failure','unavailable');w.reset();}for(const s of oldSegments){s.emit('retire',{id:1,reason:'obsolete'});s.emit('activity',{active:true});s.emit('segment',{id:1,final:true,pcm:Buffer.alloc(640)});}
 assert.equal(events.length,before);assert.equal(m.slots.get('a').source,source);assert.equal(f.calls.length,0);
 workers.at(-1).audio(Buffer.concat([tone(400),tone(900,0)]));await m.configure('a',{kind:'process',id:'11:101',pid:11,started:'101',name:'Replacement'});f.calls.shift().resolve('old application');await tick();assert.equal(f.captions().length,0);workers.at(-1).audio(Buffer.concat([tone(400),tone(900,0)]));f.calls.shift().resolve('obsolete partial');await tick();assert.equal(f.captions().length,0);assert.equal(f.calls.length,1);f.calls.shift().resolve('replacement');await tick();assert.equal(f.captions()[0].source.name,'Replacement');
 }finally{await m.close();f.q.close();}
});
test('shutdown aborts the owned request, removes timers and cannot publish a late result',async()=>{
 const f=fixture();f.q.add(A,f.job(1));f.q.add(B,f.job(1));const pending=f.calls.shift();f.q.close();assert.equal(pending.signal.aborted,true);assert.equal(f.clock.timers.size,0);const states=f.states.length;pending.resolve('after shutdown');await tick();assert.equal(f.captions().length,0);assert.equal(f.states.length,states);assert.equal(f.q.sources.size,0);
});
test('retired queued work does not consume an inference call or block another source',async()=>{
 const f=fixture();try{f.q.add(A,f.job(1));f.q.add(B,f.job(1,false));f.q.retire(B,2,'A newer segment was rejected');f.calls.shift().resolve('A');await tick();assert.equal(f.calls.length,0);assert.equal(f.q.sources.get('b').partial,null);assert.equal(f.captions().length,1);assert.equal(f.clock.timers.size,0);}finally{f.q.close();}
});

test('source health prefers detected speech over stale inference waiting without masking failures',()=>{
 let health=sourceHealth(null,{component:'inference',state:'waiting',message:'Audio processed; no recognizable speech.',recovered:true});
 health=sourceHealth(health,{component:'capture',state:'capturing',message:'Capture active — speech detected.'});
 assert.equal(health.state,'capturing');assert.match(health.message,/speech detected/);
 health=sourceHealth(health,{component:'inference',state:'unavailable',message:'Local transcription failed.'});
 assert.equal(health.state,'unavailable');
 health=sourceHealth(health,{component:'capture',state:'capturing',message:'Capture active — speech detected.'});
 assert.equal(health.state,'unavailable','active capture must not hide inference failure');
 health=sourceHealth(health,{component:'inference',state:'waiting',message:'Audio processed; no recognizable speech.',recovered:true});
 assert.equal(health.state,'capturing','successful inference recovery restores current capture activity');
});
