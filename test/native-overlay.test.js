'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {EventEmitter}=require('node:events');
const {PassThrough}=require('node:stream');
const {launchOverlay}=require('../windows-captions/native-overlay');
function fixture(exists=true){const calls=[],events=[];const spawnChild=(exe,args,options)=>{const child=new EventEmitter();child.stdin=new PassThrough();child.stdout=new PassThrough();child.exitCode=null;child.signalCode=null;child.kill=()=>{child.exitCode=0;child.emit('exit',0);};child.stdin.once('finish',()=>setImmediate(()=>{child.exitCode=0;child.emit('exit',0);}));calls.push({exe,args,options,child});return child;};return {calls,events,options:{url:'http://127.0.0.1:8090/?token=private-session',publish:e=>events.push(e),exe:'CaptionOverlay.exe',exists:()=>exists,spawnChild}};}
test('native overlay starts once with private pipe handoff and closes the owned child',async()=>{
 const f=fixture(),overlay=launchOverlay(f.options);assert.equal(f.calls.length,1);
 assert.deepEqual(f.calls[0].args,[]);assert.equal(f.calls[0].options.env,undefined);
 assert.deepEqual(f.calls[0].options.stdio,['pipe','pipe','ignore']);
 f.calls[0].child.stdout.write('{"type":"overlay-ready"}\n');assert.equal(await overlay.ready,true);
 assert.deepEqual(JSON.parse(f.calls[0].child.stdin.read().toString()),{url:f.options.url});
 assert.equal(f.calls[0].child.stdin.writableEnded,false,'Pipe remains open for parent lifetime');
 await overlay.close();assert.equal(f.calls[0].child.exitCode,0);assert.equal(f.calls.length,1,'Shutdown must not open a browser');
});
test('missing native overlay preserves browser fallback without logging the session token',async()=>{
 const f=fixture(false),overlay=launchOverlay(f.options);assert.equal(f.calls.length,1);assert.equal(f.calls[0].exe,'rundll32.exe');
 assert.match(f.events[0].message,/Native overlay missing/);assert.ok(!JSON.stringify(f.events).includes('private-session'));await overlay.close();
});
test('overlay crash and pipe failure open one fallback and leave the backend session alive',async()=>{
 const f=fixture(),overlay=launchOverlay(f.options);
 f.calls[0].child.emit('error',Error('launch failure'));f.calls[0].child.stdin.emit('error',Error('EPIPE'));f.calls[0].child.exitCode=1;f.calls[0].child.emit('exit',1);
 assert.equal(f.calls.length,2);assert.equal(f.calls[1].exe,'rundll32.exe');assert.equal(f.events.length,1);
 await overlay.close();assert.ok(!JSON.stringify(f.events).includes('private-session'));
});
