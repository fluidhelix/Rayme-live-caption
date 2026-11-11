'use strict';
const {test}=require('node:test');
const assert=require('node:assert/strict');
const fs=require('node:fs');
const os=require('node:os');
const path=require('node:path');
const {spectral,assessIsolation,capability,report}=require('./audio/common');
const {selectEndpoint}=require('./audio/windows-probe');
const firstEndpoint={id:'first-device',name:'Other playback device'};
const defaultEndpoint={id:'usb-speakers',name:'Speakers (6- USB PnP Audio Device)'};
test('audio probe chooses the Windows multimedia default regardless of enumeration order',()=>{
 for(const endpoints of [[firstEndpoint,defaultEndpoint],[defaultEndpoint,firstEndpoint]]) {
  const selected=selectEndpoint({endpoints,defaultEndpoint});
  assert.equal(selected.id,defaultEndpoint.id);assert.equal(selected.name,defaultEndpoint.name);
  assert.match(selected.reason,/Windows default multimedia.*eRender, eMultimedia/);
 }
 // Re-query results select the current default, rather than caching an earlier device.
 assert.equal(selectEndpoint({endpoints:[firstEndpoint,defaultEndpoint],defaultEndpoint:firstEndpoint}).id,firstEndpoint.id);
});
test('AUDIO_TEST_ENDPOINT overrides the default, including when no default is available',()=>{
 for(const currentDefault of [defaultEndpoint,null]) {
  assert.deepEqual(selectEndpoint({endpoints:[firstEndpoint,defaultEndpoint],defaultEndpoint:currentDefault},firstEndpoint.id),
   {...firstEndpoint,reason:'Explicit AUDIO_TEST_ENDPOINT override'});
 }
});
test('an inactive explicit endpoint fails rather than falling back to the Windows default',()=>{
 assert.throws(()=>selectEndpoint({endpoints:[defaultEndpoint],defaultEndpoint},'missing-device'),/AUDIO_TEST_ENDPOINT is not active/);
});
test('an unavailable Windows default never falls back to the first active endpoint',()=>{
 const error={hresult:'0x80070490',message:'No default endpoint'};
 for(const endpoints of [[firstEndpoint],[]]) {
  const selected=selectEndpoint({endpoints,defaultEndpoint:null,defaultEndpointError:error});
  assert.equal(selected.id,null);assert.equal(selected.name,null);assert.match(selected.reason,/unavailable/);assert.deepEqual(selected.error,error);
 }
 const selected=selectEndpoint({endpoints:[firstEndpoint],defaultEndpoint});
 assert.equal(selected.id,null);assert.match(selected.reason,/no longer active/);
});
test('malformed endpoint discovery is a test failure',()=>{
 assert.throws(()=>selectEndpoint({defaultEndpoint}),/Malformed environment response/);
});
function tone(hz){const b=Buffer.alloc(32000);for(let i=0;i<16000;i++)b.writeInt16LE(Math.round(3000*Math.sin(2*Math.PI*hz*i/16000)),i*2);return b;}
test('integration diagnostics DSP distinguishes two tones, silence, and mixed leakage',()=>{
 const a=tone(440),b=tone(997),mixed=Buffer.alloc(a.length);for(let i=0;i<a.length;i+=2)mixed.writeInt16LE(a.readInt16LE(i)+b.readInt16LE(i),i);
 const m=spectral(a);assert.ok(m.rms>2000);assert.ok(m.tones[0]>2900);assert.ok(m.tones[1]<1);
 assert.ok(spectral(b).tones[1]>2900);assert.ok(spectral(mixed).tones.every(x=>x>2900));assert.equal(spectral(Buffer.alloc(32000)).rms,0);
});
test('only enumerated environmental failures qualify as unavailable; unknown errors are failures',()=>{
 assert.equal(capability({hresult:'0x80070426'}),true);assert.equal(capability({hresult:'0x80004002'}),true);assert.equal(capability({unsupported:true}),true);
 assert.equal(capability({hresult:'0xDEADBEEF'}),false);assert.equal(capability({message:'activation failed'}),false);assert.equal(capability({hresult:'0x80004005'}),false);
});
test('diagnostics never promote INCONCLUSIVE to PASS and failures take precedence',()=>{
 const root=fs.mkdtempSync(path.join(os.tmpdir(),'audio-report-'));
 try{const file=path.join(root,'result.json');let r=report(file,[{check:'endpoint',outcome:'INCONCLUSIVE',evidence:'none'}],{quiet:true});assert.equal(r.outcome,'INCONCLUSIVE');assert.equal(JSON.parse(fs.readFileSync(file)).outcome,'INCONCLUSIVE');r=report(file,[{check:'environment',outcome:'INCONCLUSIVE',evidence:'none'},{check:'assertion',outcome:'FAIL',evidence:'wrong signal'}],{quiet:true});assert.equal(r.outcome,'FAIL');}finally{fs.rmSync(root,{recursive:true});}
});

test('real-audio assessment distinguishes unavailable rendering from production silence and cross-source leakage',()=>{
 const a=spectral(tone(440)),b=spectral(tone(997)),mix={samples:16000,rms:3000,tones:[3000,3000]},silent=spectral(Buffer.alloc(32000));
 assert.equal(assessIsolation([a,b,mix],mix).outcome,'PASS');
 assert.equal(assessIsolation([null,null,mix],mix,false).outcome,'PASS');
 assert.equal(assessIsolation([a,b,mix],silent).outcome,'INCONCLUSIVE');
 assert.equal(assessIsolation([a,b,mix],{...mix,sampleRate:48000}).outcome,'INCONCLUSIVE');
 assert.equal(assessIsolation([a,b,silent],mix).outcome,'FAIL');
 assert.equal(assessIsolation([silent,b,mix],mix).outcome,'FAIL');
 assert.equal(assessIsolation([mix,b,mix],mix).outcome,'FAIL');
});
