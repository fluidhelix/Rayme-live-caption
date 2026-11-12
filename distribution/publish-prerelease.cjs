'use strict';
const fs=require('node:fs');
const path=require('node:path');
const crypto=require('node:crypto');
const assert=require('node:assert/strict');
module.exports=async function publish({github,context,directory,gpuVerified=false}){
  assert.equal(context.ref,'refs/heads/main','Only the authorized main branch can publish prereleases');
  assert.ok(['push','workflow_dispatch'].includes(context.eventName));
  assert.match(context.sha,/^[a-f0-9]{40}$/);
  const version=require('./assets.json').version,name=`Rayme-Smart-Live-Captions-v${version}-win-x64.zip`;
  const proof=JSON.parse(fs.readFileSync(path.join(directory,'VALIDATION.json')));
  assert.equal(proof.commit,context.sha);assert.equal(proof.channel,'prerelease');
  for(const check of ['packageIntegrity','portableRuntime','packagedRealInference','nativeGuiLifecycle'])assert.equal(proof[check],true,'Missing critical package validation: '+check);
  const zip=path.join(directory,name),sums=path.join(directory,'SHA256SUMS.txt');
  const sizes=JSON.parse(fs.readFileSync(path.join(directory,'PACKAGE-SIZE.json')));assert.ok(sizes.unpackedBytes>0);
  const hash=crypto.createHash('sha256').update(fs.readFileSync(zip)).digest('hex');
  assert.equal(fs.readFileSync(sums,'utf8').trim(),`${hash}  ${name}`);
  const tag=`v${version}-rc.${context.runNumber}`;assert.match(tag,/^v1\.0\.0-rc\.\d+$/);
  const repo=context.repo;
  try{await github.rest.git.getRef({...repo,ref:'tags/'+tag});throw Error('An existing release candidate tag must not be overwritten. Start a new validated run.');}catch(e){if(e.status!==404)throw e;}
  const annotated=await github.rest.git.createTag({...repo,tag,message:`Validated Rayme prerelease ${context.sha}\nSHA256 ${hash}\nStable publication is not authorized.`,object:context.sha,type:'commit',tagger:{name:'github-actions[bot]',email:'41898282+github-actions[bot]@users.noreply.github.com',date:new Date().toISOString()}});
  await github.rest.git.createRef({...repo,ref:'refs/tags/'+tag,sha:annotated.data.sha});
  const notes=fs.readFileSync('distribution/RELEASE_NOTES.md','utf8')+`\n\nValidated commit: \`${context.sha}\`. Portable ZIP: ${(fs.statSync(zip).size/1024/1024).toFixed(1)} MiB; extracted application: ${(sizes.unpackedBytes/1024/1024).toFixed(1)} MiB, excluding the separately downloaded model.\n\nNVIDIA hardware inference: **${gpuVerified?'passed the optional real-CUDA runner test (see workflow evidence)':'unverified on hosted CPU CI; physical RTX 4080 acceptance remains required'}**.\n\nDownload the named portable ZIP, not GitHub’s automatic source archives. Stable promotion is disabled.\n`;
  const release=await github.rest.repos.createRelease({...repo,tag_name:tag,target_commitish:context.sha,name:`Rayme ${version} — ${tag} acceptance prerelease`,body:notes,draft:true,prerelease:true,make_latest:'false'});
  for(const file of [zip,sums]){
    const bytes=fs.readFileSync(file);const asset=await github.rest.repos.uploadReleaseAsset({...repo,release_id:release.data.id,name:path.basename(file),data:bytes,headers:{'content-type':file===zip?'application/zip':'text/plain','content-length':bytes.length}});
    assert.equal(asset.data.size,bytes.length,'Release upload size mismatch');
  }
  const published=await github.rest.repos.updateRelease({...repo,release_id:release.data.id,draft:false,prerelease:true,make_latest:'false'});
  return published.data.html_url;
};
