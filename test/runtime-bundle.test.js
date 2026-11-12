'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const os=require('node:os');
const {identity,seal,verify,inputFiles}=require('../distribution/runtime-bundle');
const findRuntime=require('../distribution/find-runtime.cjs');
const tools={cl:'19.44',link:'14.44',lib:'14.44',rc:'10.0.26100',mt:'10.0.26100',cmake:'cmake version 4.0',ninja:'1.12',windowsSdk:'10.0.26100.0',vcTools:'14.44',visualStudio:'17.14',node:'v22.23.5',ambientFlagsSha256:'0'.repeat(64)};
function fixture(){const root=fs.mkdtempSync(path.join(os.tmpdir(),'Rayme runtime unit é ')),repo=path.join(root,'repo'),bundle=path.join(root,'bundle');for(const file of [...inputFiles,'distribution/assets.json']){fs.mkdirSync(path.dirname(path.join(repo,file)),{recursive:true});fs.copyFileSync(path.join(__dirname,'..',file),path.join(repo,file));}return{root,repo,bundle,close:()=>fs.rmSync(root,{recursive:true,force:true})};}
test('native reuse key ignores application edits and includes compiler/SDK versions and native source inputs',async()=>{
 const f=fixture();try{
  const first=await identity(f.repo,tools);fs.mkdirSync(path.join(f.repo,'native/RaymeLauncher'),{recursive:true});fs.writeFileSync(path.join(f.repo,'native/RaymeLauncher/Program.cs'),'app-only edit');assert.equal((await identity(f.repo,tools)).key,first.key);
  for(const name of ['cl','link','lib','rc','mt','cmake','ninja','windowsSdk','vcTools','visualStudio','node','ambientFlagsSha256'])assert.notEqual((await identity(f.repo,{...tools,[name]:tools[name]+'-changed'})).key,first.key,name);
  for(const file of inputFiles){const name=path.join(f.repo,file),before=fs.readFileSync(name);fs.appendFileSync(name,'changed');assert.notEqual((await identity(f.repo,tools)).key,first.key,file);fs.writeFileSync(name,before);}
  const file=path.join(f.repo,'distribution/assets.json'),pins=JSON.parse(fs.readFileSync(file));pins.cuda.architectures.push('90');fs.writeFileSync(file,JSON.stringify(pins));assert.notEqual((await identity(f.repo,tools)).key,first.key);
  await assert.rejects(identity(f.repo,{...tools,cl:''}),/Missing compiler/);
 }finally{f.close();}
});
async function bundleFixture(){const f=fixture(),info=await identity(f.repo,tools);for(const name of ['cpu/whisper-server.exe','cuda/whisper-server.exe','cuda/cudart64_12.dll','cuda/cublas64_12.dll','cuda/cublasLt64_12.dll','licenses/WhisperCpp.txt','licenses/NVIDIA-cudart.txt','licenses/NVIDIA-cublas.txt']){fs.mkdirSync(path.dirname(path.join(f.bundle,name)),{recursive:true});fs.writeFileSync(path.join(f.bundle,name),'Unit bytes: not native compilation or inference');}await seal(f.bundle,info,'a'.repeat(40));return{...f,info};}
test('reusable native bundle verifies every file and rejects tampering, extras, missing files and wrong key',async()=>{
 const f=await bundleFixture();try{
  assert.equal((await verify(f.bundle,f.info.key)).producerCommit,'a'.repeat(40));
  await assert.rejects(verify(f.bundle,'whisper-win-x64-v1-'+'0'.repeat(64)));
  const exe=path.join(f.bundle,'cpu/whisper-server.exe'),bytes=fs.readFileSync(exe);fs.appendFileSync(exe,'bad');await assert.rejects(verify(f.bundle,f.info.key),/SHA256 mismatch/);fs.writeFileSync(exe,bytes);
  fs.writeFileSync(path.join(f.bundle,'unexpected.env'),'private');await assert.rejects(verify(f.bundle,f.info.key),/Unlisted/);fs.unlinkSync(path.join(f.bundle,'unexpected.env'));
  fs.unlinkSync(exe);await assert.rejects(verify(f.bundle,f.info.key),/Missing native/);
 }finally{f.close();}
});
function lookupFixture(){const key='whisper-win-x64-v1-'+'a'.repeat(64),context={repo:{owner:'owner',repo:'repo'}},runs=new Map(),artifacts=[];return{key,context,runs,artifacts,github:{rest:{actions:{listArtifactsForRepo:async()=>({data:{artifacts}}),getWorkflowRun:async({run_id})=>({data:runs.get(run_id)})}}},add(id,run){artifacts.push({name:'rayme-'+key,expired:false,workflow_run:{id}});runs.set(id,{id,event:'push',head_branch:'feature/windows-loopback-captions',head_repository:{full_name:'owner/repo'},path:'.github/workflows/windows-captions.yml',conclusion:'failure',...run});}};}
test('runtime artifact reuse accepts native output despite later app failure, rejecting PR/fork/unrelated producers',async()=>{
 const f=lookupFixture();f.add(1,{event:'pull_request'});f.add(2,{head_repository:{full_name:'attacker/fork'}});f.add(3,{path:'.github/workflows/unrelated.yml'});
 assert.equal(await findRuntime({...f,attempts:1,allowMissing:true}),null);f.add(4,{});assert.equal(await findRuntime({...f,attempts:1}),4);
});
test('a PR waits for the trusted push artifact and never substitutes a compilation fallback',async()=>{
 const f=lookupFixture();let waited=0;assert.equal(await findRuntime({...f,attempts:2,wait:async()=>{waited++;f.add(10,{});}}),10);assert.equal(waited,1);
 const missing=lookupFixture();await assert.rejects(findRuntime({...missing,attempts:1}),/no PR compilation fallback/);
});
