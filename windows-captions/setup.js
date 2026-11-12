'use strict';
const fs=require('node:fs');
const fsp=fs.promises;
const path=require('node:path');
const crypto=require('node:crypto');
const {execFile}=require('node:child_process');
const {promisify}=require('node:util');
const catalog=require('./models.json');
async function checkModel(file){
  if(typeof file!=='string'||file.length>4096||!path.isAbsolute(file))throw Error('Choose an existing local ggml Whisper model file.');
  let info,handle;try{info=await fsp.stat(file);handle=info.isFile()?await fsp.open(file,'r'):null;}catch{throw Error('The selected model is missing or unreadable. Choose it again in Speech setup.');}
  try{if(!info.isFile()||info.size<1024)throw Error('This file is not a complete Whisper model.');const header=Buffer.alloc(4);await handle.read(header,0,4,0);if(header.readUInt32LE(0)!==0x67676d6c)throw Error('Choose a whisper.cpp ggml .bin model, not a GGUF model or download shortcut.');}finally{await handle?.close();}
  return info.size;
}
async function digest(file,algorithm,signal){const hash=crypto.createHash(algorithm);for await(const bytes of fs.createReadStream(file,{signal}))hash.update(bytes);return hash.digest('hex');}
async function checkRuntime(exe,signal){
  const dir=path.dirname(exe);
  try{const result=await promisify(execFile)(exe,['--help'],{cwd:dir,windowsHide:true,timeout:30000,maxBuffer:65536,signal,env:{...process.env,PATH:dir+path.delimiter+process.env.PATH}});if(!/--model/.test(result.stdout+result.stderr))throw Error();}
  catch{throw Error('The bundled CPU speech runtime could not start. Re-extract the complete Rayme ZIP; do not copy just Rayme.exe.');}
}
class SetupManager{
  constructor({root,exe,publish,startModel,fetchFile=fetch,runtimeCheck=checkRuntime,models=catalog.models}){
    Object.assign(this,{root,exe,publish,startModel,fetchFile,runtimeCheck,models});this.file=path.join(root,'runtime.json');this.abort=new AbortController();this.busy=false;
    this.mode='auto';this.model=null;
    this.state={type:'setup',required:true,phase:'checking',processingMode:'auto',message:'Checking the local speech runtime…',models:models.map(({id,name,approxBytes,requiredDiskBytes,license})=>({id,name,approxBytes,requiredDiskBytes,license})),downloaded:0,total:0};
  }
  update(values){this.state={...this.state,...values};this.publish(this.state);}
  async initialize(){
    this.runtimeReady=false;this.update({required:true,phase:'checking',message:'Checking the local CPU speech runtime…'});
    await fsp.mkdir(this.root,{recursive:true});
    try{await this.runtimeCheck(this.exe,this.abort.signal);this.runtimeReady=true;}
    catch(e){if(!this.abort.signal.aborted)this.update({phase:'error',message:e.message});return;}
    let selected;try{const data=await fsp.readFile(this.file,'utf8');if(data.length<8192){const saved=JSON.parse(data);selected=saved.model;this.mode=['auto','cuda','cpu'].includes(saved.processingMode)?saved.processingMode:'auto';this.update({processingMode:this.mode});}}catch{}
    if(selected){this.model=selected;await this.select(selected);}else this.update({phase:'choose',message:'Choose your existing ggml model or download Large-v3-Turbo. Auto prefers compatible NVIDIA CUDA and safely falls back to CPU. No capture starts during setup.'});
  }
  async setMode(mode){if(!['auto','cuda','cpu'].includes(mode))throw Error('Unknown processing mode.');if(this.busy)throw Error('Wait for setup to finish before switching processing modes.');this.mode=mode;this.update({processingMode:mode});if(this.model)await this.select(this.model);else{await fsp.writeFile(this.file+'.tmp',JSON.stringify({version:1,model:null,processingMode:mode}));await fsp.rename(this.file+'.tmp',this.file);this.update({phase:'choose',message:'Choose a model to initialize the selected processing device.'});}}
  async select(file){
    if(this.busy||!this.runtimeReady)throw Error('Wait for the current setup operation or retry the CPU runtime check.');
    this.busy=true;
    try{await this.load(file);}catch(e){if(!this.abort.signal.aborted)this.update({required:true,phase:'error',message:e.message});}finally{this.busy=false;}
  }
  async load(file){
    await checkModel(file);this.update({required:true,phase:'loading',message:'Loading the selected model locally. Large models can take a few minutes. Capture remains disabled.'});
    const processing=await this.startModel(file,this.mode);
    if(this.abort.signal.aborted)return;
    this.model=file;await fsp.writeFile(this.file+'.tmp',JSON.stringify({version:1,model:file,processingMode:this.mode},null,2)+'\n');await fsp.rename(this.file+'.tmp',this.file);
    this.update({required:false,phase:'ready',processingMode:this.mode,processing,message:'Speech recognition is ready. Enable your audio sources in Controls. '+(processing?.message||'')});
  }
  async download(id){
    if(this.busy||!this.runtimeReady)throw Error('Wait for setup to finish before retrying.');
    const model=this.models.find(m=>m.id===id);if(!model)throw Error('Unknown supported model.');
    this.busy=true;
    const final=path.join(this.root,model.file),part=final+'.part';
    try{
      if(fs.existsSync(final)&&await digest(final,'sha1',this.abort.signal)===model.sha1){await this.load(final);return;}
      let offset=fs.existsSync(part)?(await fsp.stat(part)).size:0;if(offset>=model.maxBytes){await fsp.unlink(part);offset=0;}
      if(offset){
        this.update({phase:'verifying',message:'Checking the saved download before resuming…'});
        if(await digest(part,'sha1',this.abort.signal)===model.sha1){await fsp.rename(part,final);await this.load(final);return;}
      }
      const disk=await fsp.statfs(this.root);if(Number(disk.bavail)*Number(disk.bsize)<model.requiredDiskBytes)throw Error('Not enough free disk space. Allow at least 2.2 GB for the model, or choose an existing model.');
      this.update({phase:'downloading',message:'Downloading the official Whisper model. You may cancel and retry; valid partial downloads are retained.',downloaded:offset,total:model.approxBytes});
      const response=await this.fetchFile(model.url,{headers:offset?{Range:`bytes=${offset}-`}:{},signal:this.abort.signal});
      const rejectResponse=async message=>{await response.body?.cancel();throw Error(message);};
      if(!response.ok){if(response.status===416&&offset){await fsp.unlink(part);await rejectResponse('The saved partial download could not be resumed and was removed. Retry to download a fresh verified model.');}await rejectResponse(`The model download server returned HTTP ${response.status}. Check your connection and retry.`);}
      if(offset&&response.status===206){if(!response.headers.get('content-range')?.startsWith(`bytes ${offset}-`)){await fsp.unlink(part);await rejectResponse('The server returned an invalid resume position. Retry to download a fresh verified model.');}}
      else offset=0;
      const total=offset+Number(response.headers.get('content-length')||model.approxBytes-offset);
      if(total>model.maxBytes)await rejectResponse('The download is larger than the pinned model permits.');
      const handle=await fsp.open(part,offset?'a':'w');let count=offset,last=0;
      try{for await(const bytes of response.body){this.abort.signal.throwIfAborted();count+=bytes.length;if(count>model.maxBytes)throw Error('Model download exceeded its size limit.');await handle.writeFile(bytes);if(Date.now()-last>250){last=Date.now();this.update({downloaded:count,total});}}}finally{await handle.close();}
      this.update({phase:'verifying',downloaded:count,total:count,message:'Verifying the complete model against the pinned upstream hash…'});
      if(await digest(part,'sha1',this.abort.signal)!==model.sha1){await fsp.unlink(part);throw Error('The downloaded model failed its upstream checksum. The damaged file was removed; retry the download.');}
      await fsp.rename(part,final);await this.load(final);
    }catch(e){if(!this.abort.signal.aborted)this.update({required:true,phase:'error',message:e.name==='TypeError'?'Could not reach the official model server. Check your Internet connection and retry.':e.message});}
    finally{this.busy=false;}
  }
  cancel(){this.abort.abort();}
}
module.exports={SetupManager,checkModel,digest,checkRuntime};
