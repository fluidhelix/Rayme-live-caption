'use strict';
// Reusing a compiled bundle requires its recorded compiler, not a matching
// compiler installed on the consumer. Compile misses still verify actual tools.
const fs=require('node:fs');
const path=require('node:path');
const assert=require('node:assert/strict');
const {identity}=require('./runtime-bundle');
async function resolveReference(repo,reference){
  assert.match(reference.key,/^whisper-win-x64-v1-[a-f0-9]{64}$/);
  assert.match(reference.verifiedCommit,/^[a-f0-9]{40}$/);
  assert.ok(Number.isSafeInteger(reference.verifiedRun)&&reference.verifiedRun>0);
  const info=await identity(repo,reference.toolchain);
  assert.equal(info.key,reference.key,'Native inputs differ from the verified runtime reference; validate a new native build and explicitly update its reference');
  return info;
}
if(require.main===module)(async()=>{
  const [repo,output]=process.argv.slice(2);
  const reference=JSON.parse(fs.readFileSync(path.join(repo,'distribution/verified-runtime.json')));
  const info=await resolveReference(repo,reference);
  fs.mkdirSync(path.dirname(output),{recursive:true});fs.writeFileSync(output,JSON.stringify(info,null,2)+'\n');console.log(info.key);
})().catch(e=>{console.error(e.message);process.exitCode=1;});
module.exports={resolveReference};
