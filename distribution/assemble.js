'use strict';
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const pins=require('./assets.json');
const backend=['audio.js','capture.js','main.js','native-overlay.js','server.js','setup.js','sources.js','whisper.js','models.json','overlay.html','processing.js','runtime-device.js','caption-lifecycle.js','capture-session.js'];
function hash(file){return crypto.createHash('sha256').update(fs.readFileSync(file)).digest('hex');}
function walk(dir){return fs.readdirSync(dir,{withFileTypes:true}).flatMap(e=>e.isDirectory()?walk(path.join(dir,e.name)):[path.join(dir,e.name)]);}
function assemble({repo,out,launcher,overlay,helper,node,whisper,cuda,licenses,commit,nativeRuntimeKey,nativeRuntimeProducerCommit}){
  fs.mkdirSync(out,{recursive:true});if(fs.readdirSync(out).length)throw Error('Package output must be a new empty directory');
  const copy=(from,to)=>{const dest=path.join(out,to);fs.mkdirSync(path.dirname(dest),{recursive:true});fs.copyFileSync(from,dest);};
  copy(launcher,'Rayme.exe');copy(overlay,'App/native/CaptionOverlay.exe');copy(helper,'App/native/LoopbackCapture.exe');
  copy(path.join(node,'node.exe'),'App/node/node.exe');
  for(const name of backend)copy(path.join(repo,'windows-captions',name),'App/backend/'+name);
  copy(path.join(repo,'distribution/node/package.json'),'App/backend/package.json');
  const ws=path.join(repo,'distribution/node/node_modules/ws');
  for(const file of walk(ws)){const rel=path.relative(ws,file).replaceAll('\\','/');if(rel==='LICENSE'||rel==='package.json'||/^(?:lib\/.*|index|wrapper|browser)\.(?:js|mjs)$/.test(rel))copy(file,'App/backend/node_modules/ws/'+rel);}
  for(const [kind,folder] of [['cpu',whisper],['cuda',cuda]])for(const file of walk(folder))if(/\.(?:exe|dll)$/i.test(file))copy(file,'App/whisper/'+kind+'/'+path.basename(file));
  for(const file of walk(licenses))copy(file,'Licenses/'+path.relative(licenses,file));
  copy(path.join(node,'LICENSE'),'Licenses/Node.txt');copy(path.join(ws,'LICENSE'),'Licenses/ws.txt');
  copy(path.join(repo,'distribution/README.txt'),'READ-ME-FIRST.txt');copy(path.join(repo,'distribution/RELEASE_NOTES.md'),'RELEASE-NOTES.md');
  copy(path.join(repo,'distribution/LICENSE-RAYME.txt'),'LICENSE.txt');
  const manifest={version:pins.version,channel:'prerelease',commit,node:pins.node.version,dotnet:pins.dotnetRuntime,whisper:pins.whisper.commit,cuda:pins.cuda.version,cudaArchitectures:pins.cuda.architectures,nativeRuntimeKey,nativeRuntimeProducerCommit,gpuHardwareValidation:'Not certified by hosted CI; actual backend/evidence are reported at runtime.',modelIncluded:false};
  fs.writeFileSync(path.join(out,'VERSION.json'),JSON.stringify(manifest,null,2)+'\n');
  const integrity={};for(const file of walk(out).sort())integrity[path.relative(out,file).replaceAll('\\','/')]=hash(file);
  fs.writeFileSync(path.join(out,'INTEGRITY.json'),JSON.stringify(integrity,null,2)+'\n');
  validate(out);return manifest;
}
function validate(out){
  const entries=JSON.parse(fs.readFileSync(path.join(out,'INTEGRITY.json'),'utf8'));
  for(const file of walk(out)){
    const name=path.relative(out,file).replaceAll('\\','/');
    if(name==='INTEGRITY.json')continue;
    const permitted=['Rayme.exe','READ-ME-FIRST.txt','RELEASE-NOTES.md','LICENSE.txt','VERSION.json'].includes(name)||/^Licenses\/[\w.-]+\.txt$/i.test(name)||/^App\/native\/(?:CaptionOverlay|LoopbackCapture)\.exe$/.test(name)||name==='App/node/node.exe'||/^App\/whisper\/(?:cpu|cuda)\/(?:whisper-server\.exe|(?:whisper|ggml[\w-]*|cudart64_12|cublas64_12|cublasLt64_12)\.dll)$/.test(name)||backend.some(f=>name==='App/backend/'+f)||name==='App/backend/package.json'||/^App\/backend\/node_modules\/ws\/(?:LICENSE|package\.json|(?:lib\/.*|index|wrapper|browser)\.(?:js|mjs))$/.test(name);
    if(!permitted)throw Error('Forbidden package file: '+name);
    if(entries[name]!==hash(file))throw Error('Package integrity mismatch: '+name);
    if(/\.(?:json|js|html|txt|md)$/.test(name)){
      const text=fs.readFileSync(file,'utf8');
      if(/(?:[A-Z]:\\(?:Users|a|hostedtoolcache)\\|\/workspace\/|\/home\/agent\/)/i.test(text))throw Error('Build/user path leaked into package: '+name);
    }
  }
  for(const name of Object.keys(entries))if(!fs.existsSync(path.join(out,name)))throw Error('Missing package file: '+name);
  for(const name of ['Rayme.exe','App/node/node.exe','App/native/CaptionOverlay.exe','App/native/LoopbackCapture.exe','App/whisper/cpu/whisper-server.exe','App/whisper/cuda/whisper-server.exe','App/whisper/cuda/cublas64_12.dll','App/whisper/cuda/cublasLt64_12.dll','App/whisper/cuda/cudart64_12.dll','App/backend/node_modules/ws/index.js','Licenses/OpenAI-Whisper.txt','Licenses/NAudio.txt','Licenses/DotNet.txt','Licenses/WhisperCpp.txt','Licenses/NVIDIA-cublas.txt','Licenses/NVIDIA-cudart.txt'])if(!entries[name])throw Error('Required component missing: '+name);
  return entries;
}
if(require.main===module){const options=JSON.parse(fs.readFileSync(process.argv[2],'utf8'));assemble(options);console.log('Portable allowlist and integrity validated');}
module.exports={assemble,validate,hash,walk,backend};
