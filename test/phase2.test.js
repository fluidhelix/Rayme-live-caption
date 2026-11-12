'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {EventEmitter}=require('node:events');
const {PassThrough}=require('node:stream');
const {WebSocket}=require('ws');
const {SourceScheduler,SourceManager}=require('../windows-captions/sources');
const {validateApplications,Capture}=require('../windows-captions/capture');
const {createUI}=require('../windows-captions/server');
const tick=()=>new Promise(r=>setImmediate(r));
const source=(id,generation=1,name=id)=>Object.freeze({id,generation,name,identity:`${id}:start`});
const job=(id,final=true)=>({id,final,pcm:Buffer.alloc(640),capturedAt:id*100});
function tone(ms,value=1000){const b=Buffer.alloc(ms*32);for(let i=0;i<b.length;i+=2)b.writeInt16LE(value,i);return b;}
test('discovery validates identity, missing metadata, PID reuse and duplicate sessions',()=>{
 const a={id:'1:100',pid:1,started:'100',name:'chrome'};const b={...a,id:'1:200',started:'200'};
 assert.equal(validateApplications({supported:true,apps:[a,a,b]}).apps.length,2);
 assert.throws(()=>validateApplications({supported:true,apps:[{...a,started:null}]}));assert.throws(()=>validateApplications({supported:true,apps:[{...a,id:'invented'}]}));
});
test('two sources with equal local IDs keep independent partial/final state and immutable labels',async()=>{
 const waits=[],events=[];const q=new SourceScheduler(()=>new Promise(r=>waits.push(r)),e=>events.push(e),()=>{});
 const a=source('a',1,'Chrome'),b=source('b',1,'Discord');q.attach(a);q.attach(b);
 q.add(a,job(1,false));q.add(b,job(1,false));q.add(a,job(1,true));
 waits.shift()('stale Chrome');await tick();assert.equal(events.length,0);
 waits.shift()('[laughter] Chrome');await tick();assert.equal(events[0].source.name,'Chrome');assert.equal(events[0].text,'[laughter] Chrome'); // queued final has priority
 // The returned text is a mock result; attribution comes only from the queued immutable source.
 assert.equal(events[0].final,true);waits.shift()('Discord partial');await tick();assert.equal(events[1].source.name,'Discord');assert.equal(events[1].final,false);
 q.close();
});
test('round-robin finals, exactly one active request, bounded queues and overload warnings',async()=>{
 const waits=[],order=[],events=[],warnings=[];let active=0,max=0,clock=0;
 const q=new SourceScheduler(async()=>{active++;max=Math.max(max,active);await new Promise(r=>waits.push(r));active--;return 'text';},e=>events.push(e),(s,state,m)=>warnings.push({s,state,m}),()=>clock);
 const a=source('a'),b=source('b');q.attach(a);q.attach(b);q.add(a,job(1));
 for(let i=2;i<20;i++){q.add(a,job(i));q.add(b,job(i));q.add(a,job(i,false));}
 assert.equal(q.sources.get('a').finals.length,2);assert.equal(q.sources.get('b').finals.length,2);assert.ok(warnings.length);
 clock=10000;
 while(waits.length){waits.shift()();await tick();}
 const captions=events.filter(e=>e.type==='caption');assert.equal(events.filter(e=>e.type==='segment-retired').length,32);order.push(...captions.map(e=>e.source.id));assert.deepEqual(order,['a','b','a','b','a']);assert.equal(max,1);assert.ok(events.some(e=>e.queueAgeMs>=10000));q.close();
});
test('PID reuse/restart/removal while transcription is pending never relabels or leaks stale text',async()=>{
 const waits=[],events=[];const q=new SourceScheduler(()=>new Promise(r=>waits.push(r)),e=>events.push(e),()=>{});
 const old=source('a',1,'Chrome'),fresh=source('a',2,'Edge');q.attach(old);q.add(old,job(1));q.remove('a');q.attach(fresh);q.add(old,job(2));q.add(fresh,job(1));
 waits.shift()('old text');await tick();assert.equal(events.length,0);waits.shift()('new text');await tick();assert.equal(events[0].source.name,'Edge');assert.equal(events[0].source.generation,2);q.close();
});
test('two simulated PCM workers: pause removes workers, disabled source does not capture, resume changes generation, fallback exclusive',async()=>{
 const events=[],workers=[];const q=new SourceScheduler(async()=>'*sighing*',e=>events.push(e),()=>{});
 const manager=new SourceManager({scheduler:q,publish:e=>events.push(e),createCapture:(audio,status,reset)=>{const w={closed:false,audio,async select(){reset();status('ready','capturing');},async close(){this.closed=true;}};workers.push(w);return w;}});
 const a={id:'10:100',kind:'process',pid:10,started:'100',name:'Chrome'},b={id:'20:200',kind:'process',pid:20,started:'200',name:'Discord'};
 await manager.configure('a',a);await manager.configure('b',b);
 for(const w of workers) {w.audio(tone(400));w.audio(tone(1000,0));}await tick();await tick();
 assert.deepEqual(events.filter(e=>e.type==='caption').map(e=>e.source.name).sort(),['Chrome','Discord']);
 const first=manager.slots.get('a').source.generation;await manager.pause(true);assert.ok(workers.every(w=>w.closed));const count=events.filter(e=>e.type==='caption').length;workers[0].audio(tone(2000));await tick();assert.equal(events.filter(e=>e.type==='caption').length,count);
 await manager.pause(false);assert.ok(manager.slots.get('a').source.generation>first);
 await manager.remove('a');assert.equal(manager.slots.size,1);assert.ok(manager.slots.has('b'));
 await manager.configure('device',{kind:'device',id:'default',endpoint:'default',name:'Headset'});assert.deepEqual([...manager.slots.keys()],['device']);
 await manager.configure('a',a);assert.deepEqual([...manager.slots.keys()],['a']);await manager.close();q.close();assert.ok(workers.every(w=>w.closed));
});
test('process worker termination/activation failure is terminal, never changes to device capture',async()=>{
 let child,args;let bytes=0;const states=[];const capture=new Capture('helper',pcm=>bytes+=pcm.length,(m,s)=>states.push(s),()=>{},(_exe,a)=>{args=a;child=new EventEmitter();child.stdout=new PassThrough();child.stderr=new PassThrough();child.exitCode=null;child.kill=()=>{child.exitCode=0;child.emit('exit',0);};return child;});
 await capture.select({kind:'process',pid:123,started:'999'});assert.deepEqual(args,['--process','123','--started','999']);child.stderr.write('{"type":"error","message":"activation denied"}\n');child.emit('exit',4);assert.equal(capture.retry,null);assert.equal(states.at(-1),'unavailable');child.stdout.write(tone(100));assert.equal(bytes,0);await capture.close();
});
test('clear history is server-side, broadcasts and survives reconnect; per-source partials stay separate',async()=>{
 const ui=await createUI({port:0,onRefresh:()=>{}});
 const connect=()=>new WebSocket(ui.url.replace('http:','ws:'),{origin:new URL(ui.url).origin});
 try{
 const a=source('a',1,'Chrome'),b=source('b',2,'Discord');ui.publish({type:'reset',source:a});ui.publish({type:'reset',source:b});ui.publish({type:'caption',source:a,...job(1),text:'hello'});ui.publish({type:'caption',source:a,...job(2,false),text:'A partial'});ui.publish({type:'caption',source:b,...job(1,false),text:'B partial'});
 const ws=connect();const snapshot=await new Promise((r,j)=>{ws.once('message',m=>r(JSON.parse(m)));ws.once('error',j);});assert.equal(Object.keys(snapshot.partials).length,2);
 const cleared=new Promise(r=>ws.once('message',m=>r(JSON.parse(m))));ws.send(JSON.stringify({type:'clear'}));assert.equal((await cleared).type,'cleared');assert.equal(ui.state.history.length,0);assert.deepEqual(ui.state.partials,{});ws.terminate();
 const again=connect();const snap=await new Promise(r=>again.once('message',m=>r(JSON.parse(m))));assert.equal(snap.history.length,0);assert.deepEqual(snap.partials,{});again.terminate();
 }finally{await ui.close();}
});
test('overlapping application trees are rejected without replacing a working independent source',async()=>{
 const q=new SourceScheduler(async()=>'',()=>{},()=>{});const m=new SourceManager({scheduler:q,publish:()=>{},createCapture:()=>({async select(){},async close(){}})});
 await m.configure('a',{kind:'process',id:'10:100',pid:10,started:'100',name:'Parent'});
 await assert.rejects(m.configure('b',{kind:'process',id:'11:101',pid:11,started:'101',name:'Child',ancestors:['10:100']}),/overlap/);assert.equal(m.slots.size,1);await m.close();q.close();
});

test('capture ordering derives from PCM sample offsets, independent of transport chunking',()=>{
 const {Segmenter}=require('../windows-captions/audio');const a=new Segmenter(),b=new Segmenter(),one=[],two=[];a.on('segment',j=>one.push(j.captureOffsetMs));b.on('segment',j=>two.push(j.captureOffsetMs));
 const pcm=Buffer.concat([tone(2000,0),tone(500),tone(1000,0),tone(500),tone(1000,0)]);a.push(pcm);for(let i=0;i<pcm.length;i+=137)b.push(pcm.subarray(i,i+137));assert.deepEqual(one,two);assert.ok(one[1]>one[0]);
});
