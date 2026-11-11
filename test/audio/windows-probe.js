'use strict';
const fs=require('node:fs');
const path=require('node:path');
const {spawn}=require('node:child_process');
const {report,spectral,assessIsolation,capability,readSpeech}=require('./common');
const {stopChild}=require('../../windows-captions/whisper');
const delay=ms=>new Promise(r=>setTimeout(r,ms));
class Worker {
  constructor(exe,args,capture=false){
    this.events=[];this.stderr='';this.parts=[];this.bytes=0;this.collect=false;this.capture=capture;
    this.child=spawn(exe,args,{windowsHide:true,stdio:['pipe','pipe','pipe']});this.error=null;
    this.child.on('error',e=>this.error=e);this.child.on('exit',code=>this.exit=code);this.child.stdin.on('error',()=>{});
    let text='';const lines=chunk=>{text+=chunk.toString();if(text.length>65536){this.error=Error('Oversized control output');return;}let at;while((at=text.indexOf('\n'))>=0){const line=text.slice(0,at);text=text.slice(at+1);try{this.events.push(JSON.parse(line));if(this.events.length>128)this.events.shift();}catch{this.error=Error('Invalid helper control JSON');}}};
    this.child.stdout.on('data',chunk=>{if(!capture){lines(chunk);return;}if(this.collect){this.bytes+=chunk.length;if(this.bytes>32000*12){this.error=Error('PCM collection exceeded 12-second bound');return;}this.parts.push(Buffer.from(chunk));}});
    this.child.stderr.on('data',chunk=>{if(capture)lines(chunk);else this.stderr=(this.stderr+chunk.toString()).slice(-8192);});
  }
  async event(predicate,timeout=22000){const end=Date.now()+timeout;while(Date.now()<end){if(this.error)throw this.error;const e=this.events.find(predicate);if(e)return e;if(this.exit!==undefined)throw Error(`Child exited ${this.exit}: ${JSON.stringify(this.events)} ${this.stderr}`);await delay(30);}throw Error('Controlled child timed out');}
  command(cmd){this.child.stdin.write(cmd+'\n');}
  begin(){this.parts=[];this.bytes=0;this.collect=true;}
  end(){this.collect=false;return Buffer.concat(this.parts);}
  async close(){this.command('quit');await stopChild(this.child);}
}
async function single(exe,args){const w=new Worker(exe,args);try{return await w.event(()=>true);}finally{await w.close();}}
function selectEndpoint(environment, override) {
  const endpoints=environment.endpoints;
  if(!Array.isArray(endpoints))throw Error('Malformed environment response');
  if(override){
    const endpoint=endpoints.find(d=>d.id===override);
    if(!endpoint)throw Error('Requested AUDIO_TEST_ENDPOINT is not active');
    return {id:endpoint.id,name:endpoint.name,reason:'Explicit AUDIO_TEST_ENDPOINT override'};
  }
  const endpoint=environment.defaultEndpoint;
  if(!endpoint)return {id:null,name:null,reason:'Windows default multimedia render endpoint is unavailable',error:environment.defaultEndpointError};
  if(!endpoints.some(d=>d.id===endpoint.id))return {id:null,name:null,reason:'Windows default multimedia render endpoint is no longer active'};
  return {id:endpoint.id,name:endpoint.name,reason:'Windows default multimedia render endpoint (eRender, eMultimedia)'};
}
async function probe(options){
  fs.rmSync(options.output,{force:true});
  const rows=[],workers=[],evidence={};let stage='environment';
  const add=(check,outcome,message)=>rows.push({check,outcome,evidence:message});
  const worker=(exe,args,capture)=>{const w=new Worker(exe,args,capture);workers.push(w);return w;};
  try{ await (async()=>{
    if(process.platform!=='win32'){add('Real WASAPI process loopback','INCONCLUSIVE','Not Windows. No native audio API was invoked. Run this probe on windows-latest or a Windows 11 PC.');return;}
    // Read/checksum the fixture before launching anything: missing/bad test assets are failures.
    readSpeech(options.fixture);
    for(const exe of [options.helper,options.producer])if(!fs.statSync(exe).isFile())throw Error('Missing test executable');
    const env=await single(options.producer,['--environment']);evidence.environment=env;
    if(env.error){const unavailable=capability(env.error);add('Windows audio environment',unavailable?'INCONCLUSIVE':'FAIL',JSON.stringify(env.error));if(!unavailable)return;env.endpoints=[];}
    const endpoints=env.endpoints;if(!Array.isArray(endpoints))throw Error('Malformed environment response');
    add('Rendering endpoint discovery',endpoints.length?'PASS':'INCONCLUSIVE',`${endpoints.length} active rendering endpoint(s); no services, routing, volume or security settings were changed.`);
    const selection=selectEndpoint(env,options.endpoint);evidence.endpointSelection=selection;
    const selectionOutcome=selection.id?'PASS':selection.error&&!capability(selection.error)?'FAIL':'INCONCLUSIVE';
    add('Rendering endpoint selection',selectionOutcome,JSON.stringify(selection));
    if(selectionOutcome==='FAIL')return;
    const endpoint=selection.id||'no-endpoint';
    // Even without a render endpoint, launch the controlled process and attempt actual activation.
    stage='process activation';
    const a=worker(options.producer,['--tone','440','--endpoint',endpoint]);
    const identityA=await a.event(e=>e.type==='identity');
    const reference=await single(options.producer,['--activate',String(identityA.pid)]);evidence.referenceActivation=reference;
    const ca=worker(options.helper,['--process',String(identityA.pid),'--started',identityA.started],true);
    let activated=false,activationError;
    try{await ca.event(e=>e.type==='ready');activated=true;}catch(e){activationError=e.message;}
    evidence.helperActivation={ready:activated,events:ca.events,error:activationError};
    if(!reference.ok) add('Independent process activation',capability(reference.error)?'INCONCLUSIVE':'FAIL',`Documented-API activation failed: ${JSON.stringify(reference.error)}; helper ready=${activated}; ${activationError||''}`);
    const processAvailable=reference.ok && activated;
    if(reference.ok && !activated) add('Production helper process activation','FAIL',`Helper failed although independent API activation worked: ${activationError}`);
    if(processAvailable)add('Actual production helper process activation','PASS',`Real helper reached ready for controlled PID ${identityA.pid}; independent WASAPI activation also worked. Activation alone is NOT an audio-capture pass.`);
    if(!selection.id){add('Rendered speech / isolated tones','INCONCLUSIVE',`${selection.reason}. The controlled process stayed alive and activation was attempted, but no render device was selected.`);return;}
    stage='tone rendering and process isolation';
    const b=worker(options.producer,['--tone','997','--endpoint',endpoint]);const identityB=await b.event(e=>e.type==='identity');
    let cb;
    if(processAvailable){cb=worker(options.helper,['--process',String(identityB.pid),'--started',identityB.started],true);await cb.event(e=>e.type==='ready');}
    const device=worker(options.helper,['--device',endpoint],true);await device.event(e=>e.type==='ready');
    const active=[a,b,device,...(processAvailable?[ca,cb]:[])];
    a.command('play');b.command('play');
    const played=await Promise.all([a,b].map(w=>w.event(e=>e.type==='playing'||e.type==='play-error')));
    const errors=played.filter(e=>e.type==='play-error');
    if(errors.length){add('Controlled rendering',errors.every(e=>capability(e.error))?'INCONCLUSIVE':'FAIL',JSON.stringify(errors));return;}
    await delay(1200);const referenceRun=single(options.producer,['--render-reference',endpoint]).then(value=>({value}),error=>({error}));for(const w of [processAvailable?ca:null,cb,device].filter(Boolean))w.begin();await delay(3500);const referenceResult=await referenceRun;if(referenceResult.error)throw referenceResult.error;const referenceSignal=referenceResult.value;evidence.renderReference=referenceSignal;
    for(const w of active) { if(w.error)throw w.error; if(w.exit!==undefined)throw Error(`Controlled worker exited unexpectedly: ${w.exit}`); }
    const metrics=[processAvailable?ca:null,cb,device].map(w=>w?spectral(w.end()):null);evidence.toneMetrics={process440:metrics[0],process997:metrics[1],wholeDevice:metrics[2]};
    // Check the acoustic/render environment independently before judging isolation.
    if(!referenceSignal.ok){add('Independent rendering reference',capability(referenceSignal.error)?'INCONCLUSIVE':'FAIL',JSON.stringify(referenceSignal.error));return;}
    const assessment=assessIsolation(metrics,referenceSignal.metrics,processAvailable);
    add(assessment.check,assessment.outcome,assessment.evidence);
    if(assessment.outcome!=='PASS')return;
    a.command('stop');b.command('stop');await delay(500);
    stage='licensed speech rendering';
    const speech=worker(options.producer,['--fixture',path.resolve(options.fixture),'--endpoint',endpoint]);const id=await speech.event(e=>e.type==='identity');
    const captured=processAvailable?worker(options.helper,['--process',String(id.pid),'--started',id.started],true):device;if(processAvailable)await captured.event(e=>e.type==='ready');active.push(speech);if(processAvailable)active.push(captured);
    speech.command('play');const result=await speech.event(e=>e.type==='playing'||e.type==='play-error');if(result.type==='play-error')throw Error(`Speech rendering failed: ${JSON.stringify(result)}`);
    await delay(700);captured.begin();await delay(3500);const m=spectral(captured.end());evidence.speechMetrics=m;
    if(m.samples<16000||m.rms<100)throw Error(`Licensed speech did not produce non-silent process PCM: ${JSON.stringify(m)}`);
    add(`Licensed speech WAV → real rendering → real ${processAvailable?'process':'whole-device'} capture`,'PASS',`Captured ${m.samples} PCM samples, RMS ${m.rms.toFixed(1)}. This verifies non-silent capture, not a speech-recognition accuracy score.`);
    for(const w of active){if(w.error)throw w.error;if(w.exit!==undefined)throw Error(`Controlled worker exited unexpectedly: ${w.exit}`);if(w.events.some(e=>e.type==='play-error'||e.type==='fatal'))throw Error(`Producer failed: ${JSON.stringify(w.events)}`);}
  })(); }catch(e){add(stage,'FAIL',e.message);}
  finally{for(const w of workers.reverse())try{await w.close();}catch(e){add('Owned worker cleanup','FAIL',e.message);}}
  return report(options.output,rows,{title:'Real Windows audio',...evidence});
}
if(require.main===module){const [helper,producer,fixture,output='artifacts/audio-diagnostics/windows.json',...rest]=process.argv.slice(2);probe({helper,producer,fixture,output,endpoint:process.env.AUDIO_TEST_ENDPOINT}).then(result=>{if(result.outcome==='FAIL'||(rest.includes('--require-audio')&&result.outcome!=='PASS'))process.exitCode=1;}).catch(e=>{report(output,[{check:'Probe infrastructure',outcome:'FAIL',evidence:e.message}]);process.exitCode=1;});}
module.exports={probe,Worker,selectEndpoint};
