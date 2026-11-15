'use strict';
const {test}=require('node:test'),assert=require('node:assert/strict');
const {SourceManager,SourceScheduler}=require('../windows-captions/sources');
const {createUI}=require('../windows-captions/server');
const {WebSocket}=require('ws');
const slots=['a','b','c','d','e'],tick=()=>new Promise(r=>setImmediate(r));
const descriptor=i=>({kind:'process',id:`${10+i}:${100+i}`,pid:10+i,started:String(100+i),name:'Application '+i});
const source=i=>({id:slots[i],generation:i+1,identity:descriptor(i).id,name:descriptor(i).name});
const job=(id,final=true)=>({id,final,pcm:Buffer.alloc(640),capturedAt:id,audioEndAt:100000});
test('five slots allocate only configured workers, isolate removal/PID identity and retain fallback exclusivity',async()=>{
 const workers=[],events=[];const q=new SourceScheduler(async()=>'',e=>events.push(e),()=>{});
 const m=new SourceManager({scheduler:q,publish:e=>events.push(e),createCapture:()=>{const w={closed:false,async select(){},async close(){this.closed=true;}};workers.push(w);return w;}});
 try{
  assert.equal(workers.length,0);for(let i=0;i<5;i++)await m.configure(slots[i],descriptor(i));
  assert.equal(m.slots.size,5);assert.equal(workers.length,5);
  const before=[...m.slots].map(([id,e])=>[id,e.source]);await assert.rejects(m.configure('f',descriptor(6)),/Unknown source/);assert.equal(workers.length,5);
  await m.remove('c');assert.equal(workers[2].closed,true);for(const [id,s]of before)if(id!=='c')assert.equal(m.slots.get(id).source,s);
  await m.configure('c',descriptor(8));assert.equal(m.slots.get('c').source.identity,descriptor(8).id);
  await m.pause(true);assert.ok(workers.every(w=>w.closed));await m.pause(false);assert.equal(m.slots.size,5);
  await m.configure('device',{kind:'device',id:'default',endpoint:'default',name:'Playback'});assert.deepEqual([...m.slots.keys()],['device']);assert.ok(workers.slice(0,-1).every(w=>w.closed));
  await m.configure('e',descriptor(4));assert.deepEqual([...m.slots.keys()],['e']);
 }finally{await m.close();q.close();assert.ok(workers.every(w=>w.closed));}
});
test('five sources share one inference request with round-robin bounded final queues and immutable attribution',async()=>{
 const calls=[],events=[],q=new SourceScheduler(()=>new Promise(r=>calls.push(r)),e=>events.push(e),()=>{},()=>100000);
 try{
  for(let i=0;i<5;i++)q.attach(source(i));for(let i=0;i<5;i++)q.add(source(i),job(1));for(let i=0;i<5;i++)q.add(source(i),job(2));
  assert.equal(calls.length,1);for(let n=0;n<10;n++){assert.equal(calls.length,1);calls.shift()('recognized');await tick();}
  assert.equal(calls.length,0);assert.deepEqual(events.filter(e=>e.type==='caption').map(e=>e.source.id),[...slots,...slots]);
 }finally{q.close();}
});
async function snapshot(ui){const ws=new WebSocket(ui.url.replace('http:','ws:'),{origin:new URL(ui.url).origin});try{return await new Promise((resolve,reject)=>{ws.once('message',m=>resolve(JSON.parse(m)));ws.once('error',reject);});}finally{ws.terminate();}}
test('five-source retention, revisions, removal and authenticated reconnect budgets remain independent',async()=>{
 const ui=await createUI({port:0});try{
  for(let i=0;i<5;i++)for(let id=1;id<=230;id++)ui.publish({type:'caption',source:source(i),id,revision:id,final:true,text:slots[i]+' words',capturedAt:id});
  assert.equal(ui.state.history.length,1000);for(const slot of slots)assert.equal(ui.state.history.filter(c=>c.source.id===slot).length,200);
  const replay=await snapshot(ui);assert.deepEqual(replay.history,ui.state.history);assert.ok(Buffer.byteLength(JSON.stringify(replay))<2*1024*1024);
  ui.publish({type:'caption',source:source(4),id:231,revision:231,final:false,text:'maybe',capturedAt:231});ui.publish({type:'segment-retired',source:source(4),id:231,revision:232,terminal:true,retain:true});
  assert.equal(ui.state.history.find(c=>c.source.id==='e'&&c.id===231).unfinished,true);
  ui.publish({type:'source-removed',source:source(2)});assert.equal(ui.state.history.length,800);assert.equal(ui.state.sources.c,undefined);assert.equal((await snapshot(ui)).history.length,800);
 }finally{await ui.close();}
});
test('authenticated server accepts A-E commands but never invented slots or stale restarted identities',async()=>{
 const commands=[],ui=await createUI({port:0,onConfigure:(slot,d)=>commands.push({slot,d})});let ws;
 try{
  ui.publish({type:'applications',supported:true,apps:slots.map((_,i)=>descriptor(i))});ws=new WebSocket(ui.url.replace('http:','ws:'),{origin:new URL(ui.url).origin});await new Promise(r=>ws.once('open',r));
  for(let i=0;i<5;i++)ws.send(JSON.stringify({type:'configure',slot:slots[i],enabled:true,id:descriptor(i).id}));ws.send(JSON.stringify({type:'configure',slot:'f',enabled:true,id:descriptor(0).id}));
  for(let n=0;n<30&&commands.length<5;n++)await new Promise(r=>setTimeout(r,10));assert.equal(commands.length,5);assert.deepEqual(commands.map(c=>c.slot),slots);
  ui.publish({type:'applications',supported:true,apps:[descriptor(8)]});ws.send(JSON.stringify({type:'configure',slot:'c',enabled:true,id:descriptor(2).id}));await new Promise(r=>setTimeout(r,30));assert.equal(commands.length,5);
  ws.send(JSON.stringify({type:'configure',slot:'c',enabled:true,id:descriptor(8).id}));for(let n=0;n<30&&commands.length<6;n++)await new Promise(r=>setTimeout(r,10));assert.equal(commands[5].d.id,descriptor(8).id);
 }finally{ws?.terminate();await ui.close();}
});
test('continuous final producers cannot starve quieter sources with only partial recognition',async()=>{
 const calls=[],events=[],q=new SourceScheduler(()=>new Promise(r=>calls.push(r)),e=>events.push(e),()=>{},()=>100000);
 try{
  for(let i=0;i<5;i++)q.attach(source(i));for(let i=0;i<3;i++)q.add(source(i),job(1));q.add(source(3),job(1,false));q.add(source(4),job(1,false));
  for(let n=2;n<=20;n++){
   calls.shift()('recognized');await tick();for(let i=0;i<3;i++)q.add(source(i),job(n));
  }
  const recognized=events.filter(e=>e.type==='caption').map(e=>e.source.id);
  assert.ok(recognized.indexOf('d')>=0&&recognized.indexOf('d')<=5,'D receives an inference turn despite sustained finals');
  assert.ok(recognized.indexOf('e')>=0&&recognized.indexOf('e')<=11,'E also receives a bounded inference turn');
 }finally{q.close();}
});
test('partial grants rotate independently when one source continuously produces finals',async()=>{
 const calls=[],events=[],q=new SourceScheduler(()=>new Promise(r=>calls.push(r)),e=>events.push(e),()=>{},()=>100000);
 try{
  for(let i=0;i<5;i++)q.attach(source(i));q.add(source(4),job(1));for(let i=0;i<4;i++)q.add(source(i),job(1,false));
  for(let id=2;id<=27;id++){calls.shift()('recognized');await tick();q.add(source(4),job(id));for(let i=0;i<4;i++)q.add(source(i),job(id,false));}
  const partials=events.filter(e=>e.type==='caption'&&!e.final).slice(0,4).map(e=>e.source.id);assert.deepEqual(partials,['a','b','c','d']);
 }finally{q.close();}
});
