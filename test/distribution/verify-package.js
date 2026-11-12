'use strict';
const assert=require('node:assert/strict');
const fs=require('node:fs');
const path=require('node:path');
const {execFileSync}=require('node:child_process');
const {validate}=require('../../distribution/assemble');
function imports(file){
  const b=fs.readFileSync(file);assert.equal(b.toString('ascii',0,2),'MZ');const pe=b.readUInt32LE(0x3c);assert.equal(b.readUInt32LE(pe),0x4550);const count=b.readUInt16LE(pe+6),opt=pe+24,size=b.readUInt16LE(pe+20);assert.equal(b.readUInt16LE(opt),0x20b,'x64 PE required');
  const rva=b.readUInt32LE(opt+120),sections=[];for(let i=0;i<count;i++){const at=opt+size+i*40;sections.push({va:b.readUInt32LE(at+12),length:Math.max(b.readUInt32LE(at+8),b.readUInt32LE(at+16)),raw:b.readUInt32LE(at+20)});}
  const offset=r=>{const s=sections.find(s=>r>=s.va&&r<s.va+s.length);assert.ok(s,'Valid PE RVA');return s.raw+r-s.va;};
  if(!rva)return[];let at=offset(rva);const names=[];while(b.readUInt32LE(at)||b.readUInt32LE(at+12)){const name=offset(b.readUInt32LE(at+12));names.push(b.toString('ascii',name,b.indexOf(0,name)));at+=20;}return names;
}
if(require.main===module){
  const root=path.resolve(process.argv[2]),entries=validate(root),system=process.env.SystemRoot+'\\System32';
  for(const name of Object.keys(entries).filter(n=>/\.(?:exe|dll)$/i.test(n))){
    for(const dll of imports(path.join(root,name))){assert.ok(!/^(?:vcruntime|msvcp|vcomp)/i.test(dll),'External VC++/OpenMP runtime is forbidden: '+dll);assert.ok(/^api-ms-win-/i.test(dll)||fs.existsSync(path.join(path.dirname(path.join(root,name)),dll))||fs.existsSync(path.join(system,dll)),'Missing native dependency '+dll);}
  }
  const node=path.join(root,'App/node/node.exe');assert.equal(execFileSync(node,['--version'],{encoding:'utf8',env:{...process.env,PATH:system}}).trim(),'v'+require('../../distribution/assets.json').node.version);
  for(const kind of ['cpu','cuda'])execFileSync(path.join(root,`App/whisper/${kind}/whisper-server.exe`),['--help'],{cwd:path.join(root,`App/whisper/${kind}`),env:{...process.env,PATH:system},stdio:'pipe'});
  assert.equal(JSON.parse(fs.readFileSync(path.join(root,'VERSION.json'))).modelIncluded,false);
  assert.equal(JSON.parse(fs.readFileSync(path.join(root,'App/backend/models.json'))).models[0].id,'large-v3-turbo');
  console.log('PASS: complete package manifest, allowlist, real x64 executables, CPU DLL availability, pinned bundled Node and production model catalog');
}
module.exports={imports};
