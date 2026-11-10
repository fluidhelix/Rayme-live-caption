'use strict';
const fs=require('node:fs');
const {report}=require('./common');
const [file,title]=process.argv.slice(2);
try {
  const data=JSON.parse(fs.readFileSync(file,'utf8'));
  if(!['PASS','FAIL','INCONCLUSIVE'].includes(data.outcome)||!data.rows?.length||(process.env.GITHUB_RUN_ID && data.runId!==process.env.GITHUB_RUN_ID))throw Error('Missing, invalid or stale report');
} catch {
  report(file,[{check:'Integration prerequisites / execution',outcome:'FAIL',evidence:'No valid result for this run was produced. A preceding build, download or setup step failed, or the job was interrupted. This is not a test skip or an audio PASS.'}],{title});
  process.exitCode=1;
}
