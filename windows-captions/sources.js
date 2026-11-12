'use strict';
const { Segmenter } = require('./audio');
const { Capture } = require('./capture');
const { SegmentLedger } = require('./caption-lifecycle');

// One model request globally. Every source owns its queues, final watermark and generation.
class SourceScheduler {
  constructor(infer,publish,status,now=Date.now,{maxAgeMs=15000,setTimer=setTimeout,clearTimer=clearTimeout,elapsedNow=now===Date.now?()=>performance.now():now}={}) {
    if(!Number.isFinite(maxAgeMs)||maxAgeMs<1000||maxAgeMs>120000)throw Error('Caption freshness must be between 1000 and 120000 ms.');
    Object.assign(this,{infer,publish,status,now,maxAgeMs,setTimer,clearTimer,elapsedNow});this.sources=new Map();this.last=null;this.busy=false;this.closed=false;
  }
  attach(source){this.remove(source.id);this.sources.set(source.id,{source:Object.freeze({...source}),finals:[],partial:null,finalized:0,finalJobs:new Set(),queuedSince:null,queueTimer:null,revision:0,ledger:new SegmentLedger(),displayed:null,timer:null,failed:false});}
  remove(id){const state=this.sources.get(id);if(state){this.clearTimer(state.timer);this.clearTimer(state.queueTimer);state.finals=[];state.partial=null;state.displayed=null;state.finalJobs.clear();}this.sources.delete(id);}
  health(state,value,message,recovered=false){this.status(state.source,value,message,{component:'inference',recovered});}
  age(job){return Math.max(0,this.elapsedNow()-job.freshAt);}
  retire(source,id,reason,terminal=true,revision){
    const state=this.sources.get(source.id);if(!state||state.source.generation!==source.generation)return;
    const rev=revision??++state.revision;state.ledger.mark(id,rev,{closed:terminal,cleared:true});
    if(terminal){state.finalJobs.delete(id);state.finals=state.finals.filter(j=>j.id!==id);}
    if(state.partial&&(state.partial.id<id||state.partial.id===id&&(terminal||state.partial.revision<=rev)))state.partial=null;
    if(state.displayed&&(state.displayed.id<id||state.displayed.id===id&&(terminal||state.displayed.revision<=rev))){this.clearTimer(state.timer);state.displayed=null;state.timer=null;}
    this.publish({type:'segment-retired',source:state.source,id,revision:rev,terminal,reason});this.watchQueue(state);
  }
  add(source,job){
    const state=this.sources.get(source.id);
    if(this.closed||!state||state.source.generation!==source.generation||!Number.isSafeInteger(job.id)||job.id<=0)return;
    const revision=++state.revision;
    if(!state.ledger.allows(job.id,revision,job.final)||!job.final&&job.id<=state.finalized)return;
    // Convert the approximate wall-clock audio timestamp once; elapsed age then
    // uses a monotonic clock, independent of later system clock adjustments.
    const queuedAt=this.now(),queuedMonoAt=this.elapsedNow(),audioEndAt=Number.isFinite(job.audioEndAt)?Math.min(job.audioEndAt,queuedAt):queuedAt,item={...job,revision,source:state.source,queuedAt,queuedMonoAt,audioEndAt,freshAt:queuedMonoAt-Math.max(0,queuedAt-audioEndAt)};
    if(job.final){
      if(state.finalJobs.has(job.id))return;state.finalJobs.add(job.id);
      state.finalized=Math.max(state.finalized,job.id);if(state.partial?.id<=job.id)state.partial=null;
      if(state.finals.length===2){const skipped=state.finals.shift();this.retire(source,skipped.id,'Overload: an older finalized segment was skipped.',true,skipped.revision);this.health(state,'delayed','Overloaded: an older finalized segment was skipped.');}
      state.finals.push(item);
    }else state.partial=item;
    this.watchQueue(state);this.pump();
  }
  watchQueue(state){
    this.clearTimer(state.queueTimer);state.queueTimer=null;
    if(!state.finals.length&&!state.partial){const queued=state.queuedSince!==null;state.queuedSince=null;if(queued&&this.active?.state!==state)this.health(state,'waiting','Waiting for current speech.');return;}
    if(state.queuedSince===null){state.queuedSince=this.elapsedNow();if(this.busy)this.health(state,'transcribing','Speech is queued for transcription.');}
    const delay=3001-(this.elapsedNow()-state.queuedSince);
    if(delay<=0){this.health(state,'delayed','Speech is falling behind in the shared recognizer queue.');return;}
    state.queueTimer=this.setTimer(()=>{if(!this.closed&&this.sources.get(state.source.id)===state)this.watchQueue(state);},delay);state.queueTimer?.unref?.();
  }
  prune(state){
    for(const job of [...state.finals,...(state.partial?[state.partial]:[])]){
      if(!state.ledger.allows(job.id,job.revision,job.final)||!job.final&&job.id<=state.finalized){state.finals=state.finals.filter(j=>j!==job);if(job.final)state.finalJobs.delete(job.id);if(state.partial===job)state.partial=null;continue;}
      if(this.age(job)<=this.maxAgeMs)continue;
      state.finals=state.finals.filter(j=>j!==job);if(state.partial===job)state.partial=null;
      this.retire(state.source,job.id,'Queued audio exceeded the caption freshness limit.',job.final,job.revision);this.health(state,'delayed','Stale queued audio was skipped; waiting for current speech.');
    }
    this.watchQueue(state);
  }
  async pump(){
    if(this.busy||this.closed)return;
    for(const state of this.sources.values())this.prune(state);
    const keys=[...this.sources.keys()],start=(keys.indexOf(this.last)+1)%(keys.length||1),order=keys.slice(start).concat(keys.slice(0,start));
    let key=order.find(k=>this.sources.get(k).finals.length);if(!key)key=order.find(k=>this.sources.get(k).partial);if(!key)return;
    const state=this.sources.get(key),job=state.finals.shift()||state.partial;if(job===state.partial)state.partial=null;
    this.last=key;this.busy=true;this.active={state,job};this.watchQueue(state);this.controller=new AbortController();let handled=false;const started=this.elapsedNow(),queueAgeMs=started-job.queuedMonoAt;
    this.health(state,state.failed?'recovering':queueAgeMs>3000?'delayed':'transcribing',state.failed?'Retrying local transcription; previous failure remains visible.':queueAgeMs>3000?'Transcribing delayed audio.':'Transcribing current speech.');
    try{
      const text=await this.infer(job.pcm,this.controller.signal);
      if(this.closed||this.sources.get(key)!==state||!state.ledger.allows(job.id,job.revision,job.final)||!job.final&&job.id<=state.finalized)return;
      if(this.age(job)>this.maxAgeMs){handled=true;this.retire(job.source,job.id,'Completed inference exceeded the caption freshness limit.',job.final,job.revision);this.health(state,'delayed','A stale transcription result was skipped; waiting for current speech.');return;}
      handled=true;
      if(!text?.trim())this.retire(job.source,job.id,'No recognizable speech in this transcription.',job.final,job.revision);
      else{
        state.ledger.mark(job.id,job.revision,{closed:job.final,cleared:job.final,final:job.final});
        this.publish({type:'caption',source:job.source,id:job.id,revision:job.revision,final:job.final,text,capturedAt:job.capturedAt,audioEndAt:job.audioEndAt,inferenceMs:this.elapsedNow()-started,queueAgeMs});
        if(job.final){if(state.displayed?.id<=job.id){this.clearTimer(state.timer);state.displayed=null;state.timer=null;}}
        else this.expirePartial(state,key,job);
      }
      state.failed=false;this.health(state,state.finals.length?'delayed':text?.trim()?'capturing':'waiting',state.finals.length?'Finalized audio is pending.':text?.trim()?'Transcription ready.':'Audio processed; no recognizable speech.',true);
    }catch{
      if(!this.closed&&this.sources.get(key)===state&&state.ledger.allows(job.id,job.revision,job.final)){handled=true;this.retire(job.source,job.id,'Local transcription failed.',job.final,job.revision);state.failed=true;this.health(state,'unavailable','Local transcription failed; new audio will be retried.');}
    }finally{if(job.final)state.finalJobs.delete(job.id);this.busy=false;this.active=null;this.controller=null;if(!handled&&!this.closed&&this.sources.get(key)===state&&!state.failed&&!state.finals.length&&!state.partial)this.health(state,'waiting','Waiting for current speech.');this.pump();}
  }
  expirePartial(state,key,{id,revision,source,freshAt}){
    this.clearTimer(state.timer);const display={id,revision};state.displayed=display;
    state.timer=this.setTimer(()=>{if(!this.closed&&this.sources.get(key)===state&&state.displayed===display){this.retire(source,id,'Displayed partial exceeded the freshness limit.',false,revision);this.health(state,'delayed','An unfinished caption expired; waiting for current speech.');}},Math.max(1,this.maxAgeMs-Math.max(0,this.elapsedNow()-freshAt)+1));state.timer?.unref?.();
  }
  close(){this.closed=true;for(const key of [...this.sources.keys()])this.remove(key);this.controller?.abort();}
}
class SourceManager {
  constructor({ helper, scheduler, publish, threshold = 80, createCapture, now = Date.now }) {
    Object.assign(this, { helper, scheduler, publish, threshold, now });
    this.createCapture = createCapture || ((audio, status, reset) => new Capture(helper, audio, status, reset));
    this.slots = new Map(); this.generation = 0; this.paused = false; this.closed = false;
  }
  status(source, state, message, recovered=false) { this.publish({ type: 'source-status', source, state, message, component:'capture', recovered }); }
  async remove(slot) {
    const entry = this.slots.get(slot); if (!entry) return;
    entry.captureEpoch=null;this.slots.delete(slot); this.scheduler.remove(entry.source.id);
    this.publish({ type: 'source-removed', source: entry.source }); await entry.capture?.close();
  }
  async configure(slot, descriptor) {
    if (this.closed) return;
    if (!['a', 'b', 'device'].includes(slot)) throw Error('Unknown source slot');
    if (!descriptor) { await this.remove(slot); return; }
    if ((slot === 'device') !== (descriptor.kind === 'device')) throw Error('Source mode mismatch');
    if (descriptor.kind === 'process' && (!Number.isInteger(descriptor.pid) || !/^\d+$/.test(descriptor.started))) throw Error('Invalid process identity');
    for (const [other, entry] of this.slots) {
      if (other !== slot && entry.descriptor.kind === 'process' && descriptor.kind === 'process' && (entry.descriptor.ancestors?.includes(descriptor.id) || descriptor.ancestors?.includes(entry.descriptor.id))) throw Error('Selected application process trees overlap. Choose independent sources.');
      if (other !== slot && entry.descriptor.kind === descriptor.kind && entry.descriptor.id === descriptor.id) throw Error('That application is already selected.');
    }
    for (const other of [...this.slots.keys()]) if (other === slot || (slot === 'device' ? other !== 'device' : other === 'device')) await this.remove(other);
    const source = Object.freeze({ id: slot, name: descriptor.name, identity: descriptor.id, generation: ++this.generation });
    const entry = { descriptor: Object.freeze({ ...descriptor }), source, capture: null, segmenter: null };
    this.slots.set(slot, entry);
    if (!this.paused) await this.start(entry); else this.status(source, 'stopped', 'Paused');
  }
  async start(entry) {
    const epoch=Symbol();entry.captureEpoch=epoch;
    const current=()=>!this.closed&&!this.paused&&this.slots.get(entry.source.id)===entry&&entry.captureEpoch===epoch;
    const reset = () => {
      this.scheduler.remove(entry.source.id);
      entry.source = Object.freeze({ ...entry.source, generation: ++this.generation });
      const origin=entry.source,segmenter=new Segmenter({ threshold: this.threshold });entry.segmenter=segmenter;
      const currentSegment=()=>current()&&entry.segmenter===segmenter&&entry.source===origin;
      // PCM sample offset + first-block arrival anchor gives approximate capture order.
      // Raw PCM pipe has no WASAPI QPC header; never claim sample-accurate cross-app timing.
      entry.anchor = null;
      segmenter.on('segment', job => {if(currentSegment())this.scheduler.add(origin, { ...job, capturedAt: entry.anchor + job.captureOffsetMs, audioEndAt: entry.anchor+job.captureOffsetMs+job.pcm.length/32 });});
      segmenter.on('retire', job => {if(currentSegment()){this.scheduler.retire(origin,job.id,job.reason);this.status(origin,'waiting',job.reason+' Listening for speech.');}});
      segmenter.on('activity', event=>{if(currentSegment()&&!event.rejected)this.status(origin,event.active?'capturing':'waiting',event.active?'Capture active — speech detected.':'Capture active — listening for speech.');});
      entry.push = pcm => { if(!currentSegment())return;if (entry.anchor === null) entry.anchor = this.now() - pcm.length / 32; segmenter.push(pcm); };
      this.scheduler.attach(entry.source); this.publish({ type: 'reset', source: entry.source });
    };
    entry.capture = this.createCapture(pcm => { if (current()) entry.push?.(pcm); },
      (message, state = 'recovering') => { if (current()) this.status(entry.source, state==='capturing'?'waiting':state, state==='capturing'?message+' — waiting for speech.':message,state==='capturing'); },
      () => { if (current()) reset(); });
    reset();
    await entry.capture.select(entry.descriptor.kind === 'device' ? entry.descriptor.endpoint : entry.descriptor);
  }
  async pause(value) {
    if (this.closed || this.paused === value) return;
    this.paused = value;
    for (const entry of this.slots.values()) {
      entry.captureEpoch=null;this.scheduler.remove(entry.source.id);entry.source=Object.freeze({...entry.source,generation:++this.generation}); this.publish({ type: 'reset', source: entry.source });
      if (value) { await entry.capture?.close(); entry.capture = null; this.status(entry.source, 'stopped', 'Paused — no capture worker is running'); }
      else await this.start(entry);
    }
    this.publish({ type: 'paused', paused: value });
  }
  async close() { this.closed = true; for (const slot of [...this.slots.keys()]) await this.remove(slot); }
}
module.exports = { SourceScheduler, SourceManager };
