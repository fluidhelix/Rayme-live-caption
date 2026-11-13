'use strict';
// In-memory only. The same policy runs on the server and browser; the native
// client mirrors these constants and the conservative JSON byte accounting.
(function(root){
  const limits=Object.freeze({segments:200,characters:65536,bytes:131072,text:4000});
  function text(value){let end=Math.min(value.length,limits.text);if(end<value.length&&value.charCodeAt(end-1)>=0xD800&&value.charCodeAt(end-1)<=0xDBFF&&value.charCodeAt(end)>=0xDC00&&value.charCodeAt(end)<=0xDFFF)end--;return value.slice(0,end);}
  function bytes(text){let n=0;for(let i=0;i<text.length;i++){const c=text.charCodeAt(i);n+=c<32||c>=0xD800&&c<=0xDFFF?6:c===34||c===92?2:c<128?1:c<2048?2:3;}return n;}
  function cost(c){return 256+bytes(c.text)+bytes(c.source?(c.source.name||''):'Playback output')+bytes(c.source?(c.source.identity||''):'legacy');}
  function key(c){return(c.source?.id||'legacy')+':'+(c.source?.generation||0)+':'+c.id;}
  function trim(history){
    const budgets=new Map(),kept=[];
    const ordered=history.slice().sort((a,b)=>(a.capturedAt||0)-(b.capturedAt||0));
    for(let i=ordered.length-1;i>=0;i--){const c=ordered[i],slot=c.source?.id||'legacy',b=budgets.get(slot)||{count:0,chars:0,bytes:0,full:false};budgets.set(slot,b);
      const size=cost(c);if(b.full||b.count>=limits.segments||b.chars+c.text.length>limits.characters||b.bytes+size>limits.bytes){b.full=true;continue;}
      b.count++;b.chars+=c.text.length;b.bytes+=size;kept.push(c);
    }return kept.reverse();
  }
  function replace(history,caption){return trim([...history.filter(c=>key(c)!==key(caption)),caption]);}
  const policy={limits,text,bytes,cost,key,trim,replace};
  if(typeof module==='object'&&module.exports)module.exports=policy;else root.RaymeHistory=policy;
})(typeof globalThis==='object'?globalThis:this);
