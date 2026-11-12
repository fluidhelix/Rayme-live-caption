'use strict';
const fs=require('node:fs');
const path=require('node:path');
const {execFile}=require('node:child_process');
const {promisify}=require('node:util');
const {Whisper}=require('./whisper');
const run=promisify(execFile);
const minimumDriver=572.61;
function smiPath(){const choices=[path.join(process.env.SystemRoot||'C:\\Windows','System32','nvidia-smi.exe'),path.join(process.env.ProgramFiles||'C:\\Program Files','NVIDIA Corporation','NVSMI','nvidia-smi.exe')];return choices.find(fs.existsSync);}
async function detectNvidia(signal){
  const exe=smiPath();if(!exe)return null;
  try{const {stdout}=await run(exe,['--query-gpu=name,driver_version,compute_cap,uuid','--format=csv,noheader'],{windowsHide:true,timeout:8000,maxBuffer:16384,signal});
    for(const line of stdout.trim().split(/\r?\n/)){const [name,driver,compute,uuid]=line.split(',').map(v=>v.trim());if(Number.parseFloat(driver)>=minimumDriver&&['8.6','8.9'].includes(compute)&&/^GPU-[a-f0-9-]+$/i.test(uuid))return{name,driver,compute,uuid,smi:exe};}
  }catch{}return null;
}
async function gpuProcessPresent(device,pid,signal){
  try{const {stdout}=await run(device.smi,['--query-compute-apps=pid','--format=csv,noheader,nounits'],{windowsHide:true,timeout:5000,maxBuffer:16384,signal});return stdout.split(/\r?\n/).some(line=>Number(line.trim())===pid);}catch{return false;}
}
function candidates(mode,device){
  if(!['auto','cuda','cpu'].includes(mode))throw Error('Choose Auto, NVIDIA GPU (CUDA), or CPU.');
  if(mode==='cuda'&&!device)throw Error('No compatible NVIDIA GPU/driver was detected. RTX 4080 requires an NVIDIA driver supporting CUDA 12.8 (572.61 or newer). Choose Auto/CPU or update the official NVIDIA driver.');
  return mode==='cpu'||!device?['cpu']:mode==='cuda'?['cuda']:['cuda','cpu'];
}
class ProcessingSession{
  constructor({root,publish,onFailure,create=(runtime,env,options)=>new Whisper(runtime,env,options),detect=detectNvidia,evidence=gpuProcessPresent}){Object.assign(this,{root,publish,onFailure,create,detect,evidence});this.generation=0;this.state={requested:'auto',actual:'not-started',name:'Not initialized',verified:false};}
  update(values){this.state={...this.state,...values};this.publish({type:'processing',...this.state});}
  async start(model,mode='auto'){
    await this.close();this.abort=new AbortController();const generation=this.generation;this.update({requested:mode,actual:'checking',name:'Not initialized',driver:null,fallback:'',message:'Checking the selected processing mode…',verified:false,inferenceVerified:false});this.device=mode==='cpu'?null:await this.detect(this.abort.signal);if(generation!==this.generation)throw Error('Processing startup was cancelled.');let fallback='';
    let attempts;try{attempts=candidates(mode,this.device);}catch(error){this.update({actual:'unavailable',message:error.message});throw error;}
    if(mode==='auto'&&!this.device)fallback='No compatible NVIDIA GPU/driver detected; using CPU.';
    for(const kind of attempts){
      const exe=path.join(this.root,kind,'whisper-server.exe');let native={kind:'cpu',name:'CPU'};
      const env={...process.env,WHISPER_GPU:kind==='cuda'?'true':'false'};
      if(kind==='cuda'){env.CUDA_VISIBLE_DEVICES=this.device.uuid;env.CUDA_DEVICE_ORDER='PCI_BUS_ID';}
      const candidate=this.create({exe,model},env,{onRuntimeDevice:value=>native=value});this.whisper=candidate;
      try{
        await candidate.start(message=>this.publish({type:'status',message}));if(generation!==this.generation)throw Error('Processing startup was cancelled.');if(candidate.failed)throw Error('The speech runtime exited during initialization.');
        if(kind==='cuda'&&native.kind!=='cuda')throw Error('The CUDA runtime initialized CPU instead of CUDA; GPU acceleration was not verified.');
        this.update({requested:mode,actual:kind,name:kind==='cuda'?(native.name==='CPU'?this.device.name:native.name):'CPU',driver:kind==='cuda'?this.device.driver:null,verified:false,inferenceVerified:false,fallback,message:kind==='cuda'?'CUDA backend initialized; real GPU inference is not yet verified.':'CPU model initialized. '+fallback});
        candidate.child?.once?.('exit',()=>{if(generation===this.generation&&this.whisper===candidate){this.update({actual:'stopped',name:'Not initialized',verified:false,inferenceVerified:false,message:'The speech runtime stopped unexpectedly.'});this.onFailure?.(Error('The speech runtime stopped unexpectedly. Retry Rayme or choose another processing mode.'));}});
        return this.state;
      }catch(error){await candidate.close();if(this.whisper===candidate)this.whisper=null;if(generation!==this.generation)throw error;if(kind==='cpu'||mode==='cuda'){this.update({actual:'failed',name:'Not initialized',verified:false,inferenceVerified:false,message:error.message});throw error;}fallback='CUDA initialization failed; safely using CPU. '+error.message;}
    }
  }
  async infer(audio,signal){
    const generation=this.generation,whisper=this.whisper;const text=await whisper.infer(audio,signal);if(generation!==this.generation)return text;
    if(!this.state.inferenceVerified||(this.state.actual==='cuda'&&!this.state.verified&&Date.now()-(this.lastEvidence||0)>30000)){
      this.lastEvidence=Date.now();
      if(this.state.actual==='cpu')this.update({inferenceVerified:true,verified:true,message:'Real CPU inference succeeded.'});
      else{
        this.update({inferenceVerified:true,verified:false,message:'CUDA inference succeeded; independent GPU process evidence is pending.'});
        // Device diagnostics must never delay delivery of the caption that just completed.
        this.verification=Promise.resolve(this.evidence(this.device,whisper.child.pid,this.abort.signal)).catch(()=>false).then(verified=>{if(generation===this.generation)this.update({verified,message:verified?'CUDA inference succeeded; NVIDIA reports this Whisper process on the GPU.':'CUDA inference succeeded, but NVIDIA per-process evidence is unavailable; GPU activity is not independently verified.'});});
      }
    }
    return text;
  }
  async close(){this.generation++;this.abort?.abort();const previous=this.whisper;this.whisper=null;await previous?.close();if(previous)this.update({actual:'stopped',name:'Not initialized',verified:false,inferenceVerified:false,message:'Speech runtime stopped.'});}
}
module.exports={ProcessingSession,detectNvidia,gpuProcessPresent,candidates,minimumDriver};
