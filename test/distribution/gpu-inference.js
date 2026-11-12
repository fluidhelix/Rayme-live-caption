'use strict';
// Optional real NVIDIA runner gate. No mocks, no success credit without NVIDIA process evidence.
const assert=require('node:assert/strict');
const path=require('node:path');
const {readSpeech}=require('../audio/common');
(async()=>{
  const root=path.resolve(process.argv[2]),model=path.resolve(process.argv[3]),fixture=path.resolve(process.argv[4]);
  const {ProcessingSession}=require(path.join(root,'App/backend/processing'));
  const session=new ProcessingSession({root:path.join(root,'App/whisper'),publish:e=>{if(e.type==='processing')console.log(JSON.stringify(e));}});
  try{await session.start(model,'cuda');assert.equal(session.state.actual,'cuda');const text=(await session.infer(readSpeech(fixture))).toLowerCase().replace(/[^a-z\s]/g,' ');await session.verification;const expected=require('../audio/assets.json').expectedWords;const recall=expected.filter(word=>text.split(/\s+/).includes(word)).length/expected.length;assert.ok(recall>=.8&&text.includes('country')&&text.includes('americans'),`CUDA transcription accuracy failed: ${recall}`);assert.match(text,/ask\s+not\s+what\s+your\s+country/);assert.equal(session.state.verified,true,'NVIDIA must independently report the actual Whisper PID on GPU');console.log('PASS: real CUDA inference, pinned speech accuracy assertions, native CUDA initialization and NVIDIA process evidence');}
  finally{await session.close();}
})().catch(e=>{console.error(e);process.exitCode=1;});
