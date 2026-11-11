'use strict';
// Windows-only packaged executable test: real private handoff and parent-pipe EOF.
const assert=require('node:assert/strict');
const path=require('node:path');
const {spawn}=require('node:child_process');
const {createUI}=require('../windows-captions/server');
const {launchOverlay}=require('../windows-captions/native-overlay');
(async()=>{
  assert.equal(process.platform,'win32','Run the packaged WPF lifecycle test on Windows');
  const ui=await createUI({port:0});let overlay;let spawned=0;let earlyExit=false;
  try {
    const exe=path.resolve(process.argv[2]||'artifacts/windows-overlay/CaptionOverlay.exe');
    overlay=launchOverlay({url:ui.url,exe,publish:ui.publish,spawnChild:(command,args,options)=>{assert.equal(command,exe,'Native startup must succeed without browser fallback');spawned++;return spawn(command,args,options);}});
    overlay.child.once('exit',()=>earlyExit=true);
    assert.equal(await overlay.ready,true,'Packaged WPF must acknowledge loaded controls through the owned pipe');assert.equal(earlyExit,false,'Native session must remain alive with parent pipe open');
    await overlay.close();assert.equal(overlay.child.exitCode,0,'WPF must close cleanly on parent EOF');assert.equal(spawned,1);
    console.log('PASS: packaged WPF private session startup, single owned instance, graceful EOF shutdown');
  }finally{await overlay?.close();await ui.close();}
})().catch(e=>{console.error(e.message);process.exitCode=1;});
