'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {WebSocket}=require('ws');
const {SourceScheduler}=require('../windows-captions/sources');
const {createUI}=require('../windows-captions/server');
const policy=require('../windows-captions/history-policy');
const A={id:'a',generation:1,name:'Chrome',identity:'10:100'},B={id:'b',generation:2,name:'Discord',identity:'20:200'},D={id:'device',generation:3,name:'Playback',identity:'default'};
const tick=()=>new Promise(r=>setImmediate(r));
function clock(){let now=100000,id=0;const timers=new Map();return{now:()=>now,set:(fn,ms)=>{timers.set(++id,{fn,at:now+ms});return id;},clear:id=>timers.delete(id),advance(ms){const end=now+ms;for(;;){const next=[...timers].filter(([,t])=>t.at<=end).sort((a,b)=>a[1].at-b[1].at)[0];if(!next)break;now=next[1].at;timers.delete(next[0]);next[1].fn();}now=end;},timers};}
async function fixture(){const ui=await createUI({port:0}),time=clock(),calls=[],events=[];
 const q=new SourceScheduler(()=>new Promise((resolve,reject)=>calls.push({resolve,reject})),e=>{events.push(e);ui.publish(e);},(source,state,message,meta)=>ui.publish({type:'source-status',source,state,message,...meta}),time.now,{maxAgeMs:15000,setTimer:time.set,clearTimer:time.clear});
 for(const s of[A,B])q.attach(s);
 return{ui,time,calls,events,q,add:(s,id,final=false)=>q.add(s,{id,final,pcm:Buffer.alloc(640),capturedAt:time.now(),audioEndAt:time.now()}),close:async()=>{q.close();await ui.close();}};
}
function caption(ui,source,id,text='retained conversation',final=true,revision=id){ui.publish({type:'caption',source,id,revision,text,final,capturedAt:id});}
function retire(ui,source,id,revision,terminal=false){ui.publish({type:'segment-retired',source,id,revision,terminal,retain:true});}
async function snapshot(ui){const ws=new WebSocket(ui.url.replace('http:','ws:'),{origin:new URL(ui.url).origin});try{return await new Promise((resolve,reject)=>{ws.once('message',m=>resolve(JSON.parse(m)));ws.once('error',reject);});}finally{ws.terminate();}}

test('fake clock: finals survive prolonged silence while expired draft is explicitly retained, never finalized',async()=>{
 const f=await fixture();try{
  for(const s of[A,B])for(let i=1;i<=3;i++){f.add(s,i,true);f.calls.shift().resolve('verified speech '+i);await tick();}
  f.add(A,4);f.calls.shift().resolve('Useful but unfinished');await tick();const finals=f.ui.state.history.slice();
  f.time.advance(15001);assert.equal(f.ui.state.partials.a,undefined);const draft=f.ui.state.history.at(-1);assert.equal(draft.text,'Useful but unfinished');assert.equal(draft.final,false);assert.equal(draft.unfinished,true);assert.match(f.ui.state.sources.a.message,/unfinished|draft/);
  const after=JSON.stringify(f.ui.state.history),events=f.events.length;f.time.advance(24*60*60*1000);
  assert.equal(JSON.stringify(f.ui.state.history),after);assert.equal(f.events.length,events);assert.deepEqual(f.ui.state.history.filter(c=>c.final),finals);assert.equal(f.time.timers.size,0);
  const replay=await snapshot(f.ui);assert.deepEqual(replay.history,f.ui.state.history);assert.equal(replay.history.at(-1).unfinished,true);
 }finally{await f.close();}
});
test('fake clock: stale final cannot replace retained draft; fresh final replaces it once without duplication',async()=>{
 const f=await fixture();try{
  f.add(A,1);f.calls.shift().resolve('Earlier useful words');await tick();f.add(A,1,true);f.time.advance(15001);f.calls.shift().resolve('too stale to publish');await tick();
  assert.deepEqual(f.ui.state.history.map(c=>[c.text,c.final,c.unfinished]),[['Earlier useful words',false,true]]);assert.equal(f.events.filter(e=>e.type==='caption'&&e.final).length,0);
  f.add(A,2);f.calls.shift().resolve('Next unfinished draft');await tick();f.time.advance(15001);f.add(A,2,true);f.calls.shift().resolve('Accurately corrected next sentence');await tick();
  assert.equal(f.ui.state.history.length,2);assert.equal(f.ui.state.history[1].final,true);assert.equal(f.ui.state.history[1].unfinished,undefined);assert.equal(f.ui.state.history[1].text,'Accurately corrected next sentence');
 }finally{await f.close();}
});
test('failed and empty final recognition retain useful drafts, but rejected sounds are still discarded',async()=>{
 for(const outcome of['failure','empty','rejection']){const f=await fixture();try{f.add(A,1);f.calls.shift().resolve('Readable draft');await tick();
  if(outcome==='rejection')f.q.retire(A,1,'Not enough voiced audio');
  else{f.add(A,1,true);const call=f.calls.shift();if(outcome==='failure')call.reject(Error('synthetic failure'));else call.resolve('');await tick();}
  assert.equal(f.ui.state.partials.a,undefined);assert.equal(f.ui.state.history.length,outcome==='rejection'?0:1);if(outcome!=='rejection')assert.equal(f.ui.state.history[0].unfinished,true);
  if(outcome==='failure')assert.equal(f.ui.state.sources.a.state,'unavailable');
 }finally{await f.close();}}
});
test('per-source count, character and encoded-byte budgets agree on live state and reconnect snapshots',async()=>{
 const ui=await createUI({port:0});try{
  for(const source of[A,B,D])for(let id=1;id<=235;id++)caption(ui,source,id);
  for(const s of[A,B,D]){const h=ui.state.history.filter(c=>c.source.id===s.id);assert.equal(h.length,200);assert.equal(h[0].id,36);}
  assert.equal(ui.state.history.length,600);
  for(let id=236;id<=260;id++)caption(ui,A,id,'x'.repeat(4000));
  let h=ui.state.history.filter(c=>c.source.id==='a');assert.equal(h.length,16);assert.ok(h.reduce((n,c)=>n+c.text.length,0)<=policy.limits.characters);assert.equal(ui.state.history.filter(c=>c.source.id==='b').length,200);
  for(let id=261;id<=285;id++)caption(ui,A,id,'\u0001'.repeat(4000));h=ui.state.history.filter(c=>c.source.id==='a');assert.equal(h.length,5);assert.ok(h.reduce((n,c)=>n+policy.cost(c),0)<=policy.limits.bytes);
  const replay=await snapshot(ui);assert.deepEqual(replay.history,ui.state.history);assert.ok(Buffer.byteLength(JSON.stringify(replay))<2*1024*1024);
  ui.publish({type:'cleared'});assert.equal(ui.state.history.length,0);assert.equal((await snapshot(ui)).history.length,0);
 }finally{await ui.close();}
});
test('retirement revisions, clear and source changes cannot revive drafts or erase finalized history',async()=>{
 const ui=await createUI({port:0});try{
  caption(ui,A,1,'Final',true);caption(ui,A,2,'Draft',false,5);retire(ui,A,2,4);assert.ok(ui.state.partials.a);assert.equal(ui.state.history.length,1);
  retire(ui,A,2,5);caption(ui,A,2,'obsolete',false,4);assert.equal(ui.state.partials.a,undefined);assert.equal(ui.state.history[1].unfinished,true);
  caption(ui,A,2,'fresh draft',false,6);assert.equal(ui.state.history.length,1);retire(ui,A,1,100,true);assert.equal(ui.state.partials.a.text,'fresh draft');
  ui.publish({type:'cleared'});retire(ui,A,2,7);assert.equal(ui.state.history.length,0);
  caption(ui,A,3,'retained across capture reset',false,8);retire(ui,A,3,8);const resumed={...A,generation:4};ui.publish({type:'reset',source:resumed});assert.equal(ui.state.history.length,1);
  const replacement={...A,generation:5,identity:'11:101'};ui.publish({type:'reset',source:replacement});assert.equal(ui.state.history.length,0);caption(ui,A,4,'old app',true,100);assert.equal(ui.state.history.length,0);
 }finally{await ui.close();}
});
test('clear racing an existing scheduler deadline does not reconstruct the cleared transcript',async()=>{
 const f=await fixture();try{f.add(A,1);f.calls.shift().resolve('Must stay cleared');await tick();f.ui.publish({type:'cleared'});f.time.advance(15001);assert.equal(f.ui.state.history.length,0);assert.equal(f.ui.state.partials.a,undefined);assert.equal((await snapshot(f.ui)).history.length,0);}finally{await f.close();}
});
test('same-application generation reset retains a useful draft but does not permit old-generation revival',async()=>{
 const ui=await createUI({port:0});try{caption(ui,A,1,'Draft before capture restart',false,1);const next={...A,generation:4};ui.publish({type:'reset',source:next});assert.equal(ui.state.partials.a,undefined);assert.equal(ui.state.history[0].unfinished,true);assert.equal(ui.state.history[0].final,false);caption(ui,A,1,'Old final after reset',true,100);assert.equal(ui.state.history[0].text,'Draft before capture restart');const replay=await snapshot(ui);assert.deepEqual(replay.history,ui.state.history);}finally{await ui.close();}
});
test('a missing final does not erase a superseded draft; late valid final replaces it and rejection removes only its own draft',async()=>{
 const ui=await createUI({port:0});try{
  caption(ui,A,1,'First draft without a final',false,1);caption(ui,A,2,'Second live draft',false,2);assert.equal(ui.state.history[0].unfinished,true);assert.equal(ui.state.partials.a.id,2);
  caption(ui,A,1,'Accurate first final',true,3);assert.equal(ui.state.history.length,1);assert.equal(ui.state.history[0].final,true);assert.equal(ui.state.partials.a.id,2);
  retire(ui,A,2,2);ui.publish({type:'segment-retired',source:A,id:2,revision:3,terminal:true,retain:false});assert.equal(ui.state.history.length,1);assert.equal(ui.state.history[0].text,'Accurate first final');
 }finally{await ui.close();}
});
test('bounded caption text never splits a surrogate pair at the existing native 4000-character boundary',async()=>{
 const ui=await createUI({port:0});try{caption(ui,A,1,'x'.repeat(3999)+'😀');assert.equal(ui.state.history[0].text,'x'.repeat(3999));assert.equal((await snapshot(ui)).history[0].text.length,3999);assert.equal(policy.text('text 😀'),'text 😀');}finally{await ui.close();}
});
