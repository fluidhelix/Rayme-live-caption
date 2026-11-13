'use strict';
// Real .NET ClientWebSocket against the production authenticated Node server.
// Synthetic captions only: no audio device, Whisper model or Discord account needed.
const assert = require('node:assert/strict');
const { spawn } = require('node:child_process');
const { once } = require('node:events');
const { WebSocket } = require('ws');
const { createUI } = require('../windows-captions/server');
async function run() {
  const a={id:'a',generation:1,name:'Application A',identity:'10:100'},b={id:'b',generation:2,name:'Application B',identity:'20:200'},next={...a,generation:3,name:'New Application A',identity:'11:101'};
  let ui,child,observer,refreshes=0,cleared=false,configured=false;const pauses=[];
  const status=message=>ui.publish({type:'status',message});
  const caption=(source,id,final,text,capturedAt)=>ui.publish({type:'caption',source,id,final,text,capturedAt});
  try {
    ui=await createUI({port:0,onRefresh:()=>{
      refreshes++;
      if(refreshes===1){caption(a,1,false,'[laughter]',10);caption(b,1,false,'*coughing*',20);caption(b,1,true,'*sighing*',20);caption(a,1,true,'Literal <text> [laughter]',10);caption(a,1,true,'Duplicate suppressed',10);status('stage-captions');
        ui.publish({type:'caption',source:a,id:2,revision:1,final:false,text:'retire A'});ui.publish({type:'caption',source:b,id:2,revision:1,final:false,text:'keep B'});
        ui.publish({type:'segment-retired',source:a,id:2,revision:2,terminal:false});ui.publish({type:'caption',source:a,id:2,revision:1,final:false,text:'late A'});status('stage-retired-partial');
        ui.publish({type:'caption',source:a,id:3,revision:3,final:false,text:'newer A'});ui.publish({type:'segment-retired',source:a,id:2,revision:2,terminal:true});status('stage-newer-partial');
        ui.publish({type:'source-status',source:a,component:'inference',state:'unavailable',message:'Local transcription failed.'});ui.publish({type:'reset',source:a});ui.publish({type:'segment-retired',source:b,id:2,revision:2,terminal:false});status('stage-preserved-error');}
      else {ui.publish({type:'source-removed',source:a});ui.publish({type:'reset',source:next});caption(b,2,true,'B remains attributed',25);caption(next,1,false,'New application partial',30);caption(next,1,true,'New application final',30);status('stage-reconnect');}
    },onPause:paused=>{pauses.push(paused);ui.publish({type:'paused',paused});},onConfigure:(slot,descriptor)=>{
      assert.equal(slot,'a');assert.equal(descriptor.id,'11:101');
      if(configured){caption(next,237,true,'Recovered retained tail',237);status('stage-complete');return;}
      configured=true;
      for(let id=2;id<=236;id++){caption(next,id,true,'Synthetic A history '+id,id);caption(b,id+1,true,'Synthetic B history '+(id+1),id+1);}
      caption(next,237,false,'Unfinished retained tail',237);ui.publish({type:'segment-retired',source:next,id:237,revision:1,terminal:false,retain:true});status('stage-retention');
    }});
    ui.publish({type:'applications',supported:true,apps:[{kind:'process',id:'11:101',name:'New Application A',pid:11,started:'101'}]});
    for(const source of [a,b])ui.publish({type:'source-status',source,state:'capturing',message:'Synthetic protocol fixture'});
    // Exact Origin and token validation remain enabled for native connections.
    for(const [url,origin] of [[ui.url.replace(/token=.+/,'token=wrong'),new URL(ui.url).origin],[ui.url,'http://evil.example']]) {
      const ws=new WebSocket(url.replace('http:','ws:'),{origin});let opened=false;ws.on('open',()=>opened=true);
      await new Promise(resolve=>{ws.once('error',resolve);ws.once('close',resolve);});assert.equal(opened,false);ws.terminate();
    }
    observer=new WebSocket(ui.url.replace('http:','ws:'),{origin:new URL(ui.url).origin});observer.on('message',m=>{if(JSON.parse(m).type==='cleared')cleared=true;});await once(observer,'open');
    const dotnet=process.env.DOTNET_EXE||'dotnet',dll=process.argv[2]||'native/CaptionOverlay.Tests/bin/Release/net10.0/CaptionOverlay.Tests.dll';
    child=spawn(dotnet,[dll,'--session'],{stdio:['pipe','pipe','pipe']});let output='';
    for(const stream of [child.stdout,child.stderr])stream.on('data',b=>{output=(output+b.toString()).slice(-16384);});
    child.stdin.on('error',()=>{});child.stdin.end(JSON.stringify({url:ui.url})+'\n');
    const timer=setTimeout(()=>child.kill(),40000);
    let code;try{[code]=await once(child,'exit');}finally{clearTimeout(timer);}
    assert.equal(code,0,output);assert.ok(cleared,'Clear must reach the second connected client');assert.ok(configured);assert.deepEqual(pauses,[true,false]);assert.equal(refreshes,2);
    assert.ok(!output.includes(new URL(ui.url).searchParams.get('token')),'No session credentials in test output');
    process.stdout.write(output);console.log('PASS: native authentication, controls, reconnect snapshot, and cross-client Clear History');
  }finally{child?.kill();observer?.terminate();await ui?.close();}
}
run().catch(error=>{console.error(error);process.exitCode=1;});
