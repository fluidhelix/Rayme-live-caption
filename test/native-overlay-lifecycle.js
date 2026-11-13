'use strict';
// Windows-only packaged executable test: real private handoff and parent-pipe EOF.
const assert=require('node:assert/strict');
const path=require('node:path');
const {spawn,execFile}=require('node:child_process');
const {promisify}=require('node:util');
const {createUI}=require('../windows-captions/server');
const {launchOverlay}=require('../windows-captions/native-overlay');
(async()=>{
  assert.equal(process.platform,'win32','Run the packaged WPF lifecycle test on Windows');
  const unexpected=()=>assert.fail('Layout controls must not reconfigure capture or inference');
  const ui=await createUI({port:0,onConfigure:unexpected,onPause:unexpected,onSetup:unexpected});
  for(const source of [{id:'a',name:'Synthetic Browser',identity:'test:a',generation:1},{id:'b',name:'Synthetic Conversation',identity:'test:b',generation:2}])ui.publish({type:'source-status',source,state:'waiting',message:'Synthetic UI fixture'});let overlay;let spawned=0;let earlyExit=false;
  try {
    const exe=path.resolve(process.argv[2]||'artifacts/windows-overlay/CaptionOverlay.exe');
    overlay=launchOverlay({url:ui.url,exe,publish:ui.publish,spawnChild:(command,args,options)=>{assert.equal(command,exe,'Native startup must succeed without browser fallback');spawned++;return spawn(command,args,options);}});
    overlay.child.once('exit',()=>earlyExit=true);
    assert.equal(await overlay.ready,true,'Packaged WPF must acknowledge loaded controls through the owned pipe');assert.equal(earlyExit,false,'Native session must remain alive with parent pipe open');
    const result=await promisify(execFile)('dotnet',['run','--project','native/PortablePackage.Tests','-c','Release','--','--overlay-ui',String(overlay.child.pid)],{cwd:path.join(__dirname,'..'),timeout:120000,maxBuffer:32768,windowsHide:true});
    assert.match(result.stdout,/packaged detached windows/);console.log(result.stdout.trim());
    await overlay.close();assert.equal(overlay.child.exitCode,0,'WPF must close cleanly on parent EOF');assert.equal(spawned,1);
    console.log('PASS: packaged WPF private session startup, single owned instance, graceful EOF shutdown');
  }finally{await overlay?.close();await ui.close();}
})().catch(e=>{
  // execFile's message omits the managed UI automation exception on some Node
  // versions. Keep bounded synthetic-test diagnostics visible in CI annotations.
  const diagnostic=[e.stack||e.message,e.stdout,e.stderr].filter(Boolean).join('\n')
    .replace(/https?:\/\/[^\s]+/g,'[session URL redacted]').slice(-8000);
  console.error(diagnostic);
  if(process.env.GITHUB_ACTIONS==='true')console.log('::error title=Packaged overlay lifecycle::'+diagnostic.replaceAll('%','%25').replaceAll('\r','%0D').replaceAll('\n','%0A'));
  process.exitCode=1;
});
