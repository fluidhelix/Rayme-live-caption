'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const {EventEmitter}=require('node:events');
const {PassThrough}=require('node:stream');
const {launchOverlay}=require('../windows-captions/native-overlay');
function fixture(exists=true){const calls=[],events=[],failures=[];let quits=0;const spawnChild=(exe,args,options)=>{const child=new EventEmitter();child.stdin=new PassThrough();child.stdout=new PassThrough();child.exitCode=null;child.signalCode=null;child.kill=()=>{child.exitCode=0;child.emit('exit',0);};child.stdin.once('finish',()=>setImmediate(()=>{child.exitCode=0;child.emit('exit',0);}));calls.push({exe,args,options,child});return child;};return {calls,events,failures,get quits(){return quits;},options:{url:'http://127.0.0.1:8090/?token=private-session',publish:e=>events.push(e),onQuit:()=>quits++,onFailure:e=>failures.push(e.message),exe:'CaptionOverlay.exe',exists:()=>exists,spawnChild}};}
test('native startup uses a private capability handoff and shutdown never starts a browser',async()=>{
 const f=fixture(),overlay=launchOverlay(f.options);assert.equal(f.calls.length,1);assert.deepEqual(f.calls[0].args,[]);assert.equal(f.calls[0].options.env,undefined);
 assert.deepEqual(f.calls[0].options.stdio,['pipe','pipe','ignore']);f.calls[0].child.stdout.write('{"type":"overlay-ready"}\n');assert.equal(await overlay.ready,true);
 const handoff=JSON.parse(f.calls[0].child.stdin.read().toString());assert.equal(handoff.url,f.options.url);assert.match(handoff.capability,/^[a-f0-9]{48}$/);
 assert.equal(f.calls[0].child.stdin.writableEnded,false);await overlay.close();assert.equal(f.calls[0].child.exitCode,0);assert.equal(f.calls.length,1);assert.equal(f.failures.length,0);
});
test('missing native executable reports controlled failure without any browser process',async()=>{
 const f=fixture(false),overlay=launchOverlay(f.options);assert.equal(await overlay.ready,false);assert.equal(f.calls.length,0);assert.equal(f.failures.length,1);assert.ok(!JSON.stringify(f.events).includes('private-session'));await overlay.close();
});
test('unexpected exit code zero is still a crash without explicit intent',async()=>{
 const f=fixture(),overlay=launchOverlay(f.options);f.calls[0].child.stdout.write('{"type":"overlay-ready"}\n');await overlay.ready;
 f.calls[0].child.exitCode=0;f.calls[0].child.emit('exit',0);assert.equal(f.failures.length,1);assert.equal(f.quits,0);assert.equal(f.calls.length,1);await overlay.close();
});
test('valid explicit Quit Rayme is accepted once and subsequent exit cannot open a browser',async()=>{
 const f=fixture(),overlay=launchOverlay(f.options),child=f.calls[0].child;
 const handoff=JSON.parse(child.stdin.read().toString());child.stdout.write('{"type":"overlay-ready"}\n');await overlay.ready;
 const intent=JSON.stringify({type:'quit-rayme',capability:handoff.capability})+'\n';child.stdout.write(intent.slice(0,12));child.stdout.write(intent.slice(12));child.stdout.write(intent);
 child.exitCode=0;child.emit('exit',0);assert.equal(f.quits,1);assert.equal(f.failures.length,0);assert.equal(overlay.intentional,true);assert.equal(f.calls.length,1);await overlay.close();
});
test('wrong capability and unrelated output cannot express user exit intent',async()=>{
 const f=fixture(),overlay=launchOverlay(f.options),child=f.calls[0].child;child.stdout.write('{"type":"overlay-ready"}\n');await overlay.ready;
 child.stdout.write('{"type":"quit-rayme","capability":"not-the-session-capability"}\n');assert.equal(f.quits,0);child.emit('error',Error('launch failure'));child.stdin.emit('error',Error('EPIPE'));child.exitCode=1;child.emit('exit',1);
 assert.equal(f.failures.length,1);assert.equal(f.calls.length,1);assert.ok(!JSON.stringify(f.events).includes('private-session'));await overlay.close();
});
