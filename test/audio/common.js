'use strict';
const fs = require('node:fs');
const path = require('node:path');
const crypto = require('node:crypto');
function readSpeech(file) {
  const bytes = fs.readFileSync(file), assets = require('./assets.json');
  if (crypto.createHash('sha256').update(bytes).digest('hex') !== assets.fixtureSha256) throw Error('Speech fixture checksum mismatch');
  if (bytes.toString('ascii',0,4) !== 'RIFF' || bytes.toString('ascii',8,12) !== 'WAVE') throw Error('Not a WAV file');
  let format, pcm;
  for (let at=12; at+8<=bytes.length;) {
    const n=bytes.readUInt32LE(at+4), start=at+8, end=start+n;
    if(end>bytes.length) throw Error('Truncated WAV chunk');
    const kind=bytes.toString('ascii',at,at+4);
    if(kind==='fmt ') { if(n<16) throw Error('Truncated WAV format'); format={encoding:bytes.readUInt16LE(start),channels:bytes.readUInt16LE(start+2),rate:bytes.readUInt32LE(start+4),bits:bytes.readUInt16LE(start+14)}; }
    if(kind==='data') pcm=bytes.subarray(start,end);
    at=end+(n%2);
  }
  if(!format || format.encoding!==1 || format.channels!==1 || format.rate!==16000 || format.bits!==16 || !pcm?.length || pcm.length%2) throw Error('Expected nonempty 16 kHz mono PCM16 fixture');
  return pcm;
}
function spectral(pcm, frequencies=[440,997]) {
  const n=Math.floor(pcm.length/2); if(!n) return {samples:0,rms:0,tones:frequencies.map(()=>0)};
  let power=0; const re=frequencies.map(()=>0),im=frequencies.map(()=>0);
  for(let i=0;i<n;i++){const x=pcm.readInt16LE(i*2);power+=x*x;for(let k=0;k<frequencies.length;k++){const phase=2*Math.PI*frequencies[k]*i/16000;re[k]+=x*Math.cos(phase);im[k]+=x*Math.sin(phase);}}
  return {samples:n,rms:Math.sqrt(power/n),tones:re.map((r,k)=>2*Math.hypot(r,im[k])/n)};
}
function assessIsolation(metrics, reference, requireProcesses = true) {
  if(reference.samples<(reference.sampleRate||16000) || reference.tones.some(x=>x<50)) return {outcome:'INCONCLUSIVE',check:'Independent endpoint rendering reference',evidence:`No confirmed rendered test tones: ${JSON.stringify(reference)}. No process isolation PASS is credited.`};
  if(metrics[2].samples<16000 || metrics[2].tones.some(x=>x<50)) return {outcome:'FAIL',check:'Production whole-device capture',evidence:`Missed independently confirmed rendered tones: ${JSON.stringify(metrics[2])}`};
  if(!requireProcesses)return {outcome:'PASS',check:'Real whole-device capture (process isolation unavailable)',evidence:'Independent endpoint reference and production whole-device capture both contain the rendered tones. This does not verify process isolation.'};
  for(let i=0;i<2;i++){
    const m=metrics[i],desired=m.tones[i],other=m.tones[1-i];
    if(m.samples<16000 || m.rms<100 || desired<100 || desired<other*10) return {outcome:'FAIL',check:'Two-process isolation',evidence:`Process ${i} failed its own-tone/non-silence/20 dB rejection check: ${JSON.stringify(m)}`};
  }
  return {outcome:'PASS',check:'Real endpoint rendering, production device capture and two-process isolation',evidence:'Independent reference and whole-device capture contain both tones; each process capture contains its own tone with at least 20 dB rejection of the other. No inference or PCM mocks.'};
}
const environmental = new Set(['0x80004001','0x80004002','0x80040154','0x80070032','0x80070005','0x80070426','0x80070490']);
function capability(error) { return error?.unsupported === true || environmental.has(error?.hresult?.toLowerCase()); }
function report(file, rows, detail={}) {
  if(!rows.length)rows.push({check:'Test execution',outcome:'FAIL',evidence:'No checks executed'});
  const outcome=rows.some(r=>r.outcome==='FAIL')?'FAIL':rows.some(r=>r.outcome==='INCONCLUSIVE')?'INCONCLUSIVE':'PASS';
  const result={outcome,rows,createdAt:new Date().toISOString(),runId:process.env.GITHUB_RUN_ID||null,commit:process.env.GITHUB_SHA||null,...detail};fs.mkdirSync(path.dirname(file),{recursive:true});fs.writeFileSync(file,JSON.stringify(result,null,2)+'\n');
  const md=`## ${detail.title || 'Audio integration'}: ${outcome}\n\n| Check | Outcome | Evidence |\n|---|---|---|\n`+rows.map(r=>`| ${r.check} | **${r.outcome}** | ${r.evidence.replace(/[\r\n|]/g,' ')} |`).join('\n')+'\n';
  fs.writeFileSync(file.replace(/\.json$/,'.md'),md);if(!detail.quiet){if(process.env.GITHUB_STEP_SUMMARY)fs.appendFileSync(process.env.GITHUB_STEP_SUMMARY,md+'\n');console.log(md);}
  if(!detail.quiet && outcome==='INCONCLUSIVE' && process.env.GITHUB_ACTIONS) console.log('::warning::Real audio integration is INCONCLUSIVE; see the audio diagnostics artifact. This is not an audio PASS.');
  return result;
}
module.exports={readSpeech,spectral,assessIsolation,capability,report};
