'use strict';
// Only independently verified bundles from trusted push/dispatch runs are reusable.
module.exports=async function findRuntime({github,context,key,attempts=250,allowMissing=false,wait=ms=>new Promise(r=>setTimeout(r,ms))}){
  if(!/^whisper-win-x64-v1-[a-f0-9]{64}$/.test(key))throw Error('Invalid runtime key');
  const name='rayme-'+key;
  for(let attempt=0;attempt<attempts;attempt++){
    const {data}=await github.rest.actions.listArtifactsForRepo({...context.repo,name,per_page:100});
    for(const artifact of data.artifacts){
      if(artifact.expired||artifact.name!==name||!artifact.workflow_run?.id)continue;
      const {data:run}=await github.rest.actions.getWorkflowRun({...context.repo,run_id:artifact.workflow_run.id});
      if(!['push','workflow_dispatch'].includes(run.event)||run.head_branch!=='feature/windows-loopback-captions'||run.head_repository?.full_name!==`${context.repo.owner}/${context.repo.repo}`||!/^\.github\/workflows\/windows-captions\.yml(?:@|$)/.test(run.path))continue;
      // The runtime job publishes immediately; unrelated app tests may fail later.
      return run.id;
    }
    if(attempt+1<attempts)await wait(30000);
  }
  if(allowMissing)return null;
  throw Error('No trusted runtime artifact for '+key+'. The matching push must finish its independent native-runtime job; no PR compilation fallback is allowed.');
};
