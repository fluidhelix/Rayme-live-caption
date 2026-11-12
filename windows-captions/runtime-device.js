'use strict';
// Startup-only facts, not a retained native log. Parsing stops before inference.
class RuntimeDeviceParser{
  constructor(){this.pending='';this.kind='cpu';this.name='CPU';}
  feed(chunk){this.pending+=chunk.toString();let at;while((at=this.pending.indexOf('\n'))>=0){this.line(this.pending.slice(0,at));this.pending=this.pending.slice(at+1);}if(this.pending.length>4096)this.pending=this.pending.slice(-4096);}
  line(line){
    const device=line.match(/Device\s+0:\s+(.+?),\s+compute capability/);if(device)this.name=device[1].slice(0,160);
    if(/whisper_backend_init_gpu: using CUDA\d* backend/.test(line))this.kind='cuda';
    if(/whisper_backend_init_gpu: (?:no GPU found|failed to initialize)/.test(line)){this.kind='cpu';this.name='CPU';}
  }
  finish(){this.line(this.pending);this.pending='';return{kind:this.kind,name:this.kind==='cpu'?'CPU':this.name};}
}
module.exports={RuntimeDeviceParser};
