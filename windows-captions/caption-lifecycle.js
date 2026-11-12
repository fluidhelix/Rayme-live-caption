'use strict';
// Bounded protocol watermarks. Finalized history is independent of these guards.
class SegmentLedger {
  constructor(){this.floor=0;this.finalized=0;this.latest=0;this.entries=new Map();}
  revision(id,value){return Number.isSafeInteger(value)&&value>=0?value:(this.entries.get(id)?.revision||0)+1;}
  allows(id,revision,final=false,snapshot=false){
    // Segment order limits partials only; a fresh out-of-order final is still history.
    if(!Number.isSafeInteger(id)||id<=this.floor||!final&&(id<=this.finalized||id<this.latest))return false;
    const old=this.entries.get(id);if(!old)return true;if(old.closed)return false;
    return final?revision>=old.revision:revision>old.revision||snapshot&&revision===old.revision&&!old.cleared;
  }
  mark(id,revision,{closed=false,cleared=false,final=false}={}){
    const old=this.entries.get(id);if(id<=this.floor||old&&(old.closed||!closed&&old.revision>revision))return;
    this.latest=Math.max(this.latest,id);this.entries.set(id,{id,revision:Math.max(revision,old?.revision||0),closed,cleared});if(final||closed)this.finalized=Math.max(this.finalized,id);
    while(this.entries.size>128){const first=Math.min(...this.entries.keys());this.entries.delete(first);this.floor=Math.max(this.floor,first);}
  }
  toJSON(){return{floor:this.floor,finalized:this.finalized,latest:this.latest,entries:[...this.entries.values()]};}
}
function sourceHealth(previous,event){
  const components={...previous?.components},component=event.component==='inference'?'inference':'capture',old=components[component];
  components[component]={state:event.state,message:event.message||'',issue:event.state==='unavailable'?event.message:event.recovered?'':old?.issue||'',notice:event.state==='delayed'?event.message:event.recovered?'':old?.notice||''};
  const capture=components.capture,inference=components.inference,issues=[...new Set(Object.values(components).map(c=>c.issue).filter(Boolean))];
  let current;
  if(capture?.state==='stopped')current=capture;
  else if(issues.length){current=Object.values(components).find(c=>c.state==='unavailable')||inference||capture;current={...current,state:Object.values(components).some(c=>c.state==='unavailable')?'unavailable':'recovering'};}
  else current=[capture,inference].find(c=>c?.state==='recovering')||(inference?.notice?{...inference,state:'delayed'}:null)||[inference].find(c=>['delayed','transcribing'].includes(c?.state))||(capture?.state==='capturing'?capture:null)||[capture,inference].find(c=>c?.state==='waiting')||capture||inference;
  const message=[current?.message,...issues.filter(issue=>issue!==current?.message).map(issue=>'Last failure: '+issue),inference?.notice!==current?.message?inference?.notice:''].filter(Boolean).join(' ');
  return{...event,components,state:current?.state||'waiting',message};
}
module.exports={SegmentLedger,sourceHealth};
