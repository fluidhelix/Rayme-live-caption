'use strict';
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const assert=require('node:assert/strict');
const inputFiles=['distribution/build-whisper.ps1','distribution/whisper-common.ps1','distribution/runtime-inputs.ps1','distribution/runtime-bundle.js','distribution/whisper-utf8.manifest','distribution/legal/NVIDIA-cudart.txt','distribution/legal/NVIDIA-cublas.txt'];
function canonical(value){return JSON.stringify(Array.isArray(value)?value.map(v=>JSON.parse(canonical(v))):value&&typeof value==='object'?Object.fromEntries(Object.keys(value).sort().map(k=>[k,JSON.parse(canonical(value[k]))])):value);}
const digest=text=>crypto.createHash('sha256').update(text).digest('hex');
async function hash(file){const h=crypto.createHash('sha256');for await(const chunk of fs.createReadStream(file))h.update(chunk);return h.digest('hex');}
async function identity(repo,toolchain){
  for(const field of ['cl','link','lib','rc','mt','cmake','ninja','windowsSdk','vcTools','visualStudio','node','ambientFlagsSha256'])assert.ok(typeof toolchain[field]==='string'&&toolchain[field].length>0,'Missing compiler/toolchain version: '+field);
  const pins=JSON.parse(fs.readFileSync(path.join(repo,'distribution/assets.json'))),files={};
  for(const file of inputFiles)files[file]=await hash(path.join(repo,file));
  const inputs={format:1,platform:'windows-x64',whisper:pins.whisper,cuda:pins.cuda,files,toolchain};
  return{key:'whisper-win-x64-v1-'+digest(canonical(inputs)),inputs};
}
function walk(root,prefix=''){
  return fs.readdirSync(path.join(root,prefix),{withFileTypes:true}).flatMap(e=>{assert.ok(!e.isSymbolicLink(),'Runtime symlinks are forbidden');const rel=prefix+e.name;return e.isDirectory()?walk(root,rel+'/'):[rel];}).sort();
}
function allowed(name){return name==='RUNTIME.json'||/^(cpu|cuda)\/(whisper-server\.exe|(?:whisper|ggml[\w-]*|cudart64_12|cublas64_12|cublasLt64_12)\.dll)$/.test(name)||/^licenses\/(WhisperCpp|NVIDIA-cudart|NVIDIA-cublas)\.txt$/.test(name);}
async function seal(root,info,commit){
  assert.equal(info.key,'whisper-win-x64-v1-'+digest(canonical(info.inputs)));assert.match(commit,/^[a-f0-9]{40}$/);
  fs.writeFileSync(path.join(root,'RUNTIME.json'),JSON.stringify({...info,producerCommit:commit},null,2)+'\n');
  const lines=[];for(const name of walk(root)){assert.ok(allowed(name),'Forbidden native runtime file: '+name);lines.push(await hash(path.join(root,name))+'  '+name);}
  fs.writeFileSync(path.join(root,'SHA256SUMS.txt'),lines.join('\n')+'\n');await verify(root,info.key);
}
async function verify(root,key){
  assert.match(key,/^whisper-win-x64-v1-[a-f0-9]{64}$/);
  const sumFile=path.join(root,'SHA256SUMS.txt');assert.ok(fs.statSync(sumFile).size<131072);
  const entries=new Map();for(const line of fs.readFileSync(sumFile,'utf8').trim().split(/\r?\n/)){
    const m=line.match(/^([a-f0-9]{64})  (.+)$/);assert.ok(m&&allowed(m[2])&&!entries.has(m[2]),'Invalid runtime checksum entry');entries.set(m[2],m[1]);
  }
  for(const name of walk(root)){if(name==='SHA256SUMS.txt')continue;assert.ok(entries.has(name),'Unlisted native runtime file: '+name);assert.equal(await hash(path.join(root,name)),entries.get(name),'Native runtime SHA256 mismatch: '+name);entries.delete(name);}
  assert.equal(entries.size,0,'Missing native runtime files');
  for(const file of ['cpu/whisper-server.exe','cuda/whisper-server.exe','cuda/cudart64_12.dll','cuda/cublas64_12.dll','cuda/cublasLt64_12.dll','licenses/WhisperCpp.txt','licenses/NVIDIA-cudart.txt','licenses/NVIDIA-cublas.txt'])assert.ok(fs.existsSync(path.join(root,file)),'Missing native runtime component: '+file);
  assert.ok(fs.statSync(path.join(root,'RUNTIME.json')).size<1024*1024,'Native runtime metadata is oversized');
  const info=JSON.parse(fs.readFileSync(path.join(root,'RUNTIME.json')));assert.equal(info.key,key);assert.equal(key,'whisper-win-x64-v1-'+digest(canonical(info.inputs)));assert.match(info.producerCommit,/^[a-f0-9]{40}$/);return info;
}
if(require.main===module)(async()=>{
  const [command,root,arg,extra]=process.argv.slice(2);
  if(command==='identity'){const info=await identity(root,JSON.parse(fs.readFileSync(arg)));fs.writeFileSync(extra,JSON.stringify(info,null,2)+'\n');console.log(info.key);}
  else if(command==='seal')await seal(root,JSON.parse(fs.readFileSync(arg)),extra);
  else if(command==='verify'){await verify(root,arg);console.log('PASS native runtime identity and every SHA256 file');}
  else throw Error('Unknown runtime bundle command');
})().catch(e=>{console.error(e.message);process.exitCode=1;});
module.exports={identity,seal,verify,canonical,inputFiles};
