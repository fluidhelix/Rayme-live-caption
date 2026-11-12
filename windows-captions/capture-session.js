'use strict';
// Session intent survives model reloads; SourceManager remains the capture authority.
class CaptureSession {
  constructor(){this.desired=null;this.paused=false;this.generation=0;this.manager=null;}
  async ready(manager){
    if(this.manager===manager)return;
    if(this.manager)throw Error('Stop the previous capture session before restoring sources.');
    this.manager=manager;manager.generation=this.generation;
    if(this.desired===null)this.desired=new Map([['device',{kind:'device',id:'default',endpoint:'default',name:'Windows default playback'}]]);
    // Configure while paused to avoid briefly starting a worker during restoration.
    manager.paused=this.paused;
    for(const [slot,descriptor] of this.desired)await manager.configure(slot,descriptor);
  }
  async configure(slot,descriptor){
    if(!this.manager)throw Error('Wait for the speech model to be ready.');
    await this.manager.configure(slot,descriptor);
    this.desired=new Map([...this.manager.slots].map(([id,e])=>[id,e.descriptor]));
  }
  async pause(value){if(!this.manager)throw Error('Complete speech setup first.');await this.manager.pause(value);this.paused=value;}
  async suspend(){
    if(!this.manager)return;
    const manager=this.manager;this.manager=null;
    this.desired=new Map([...manager.slots].map(([id,e])=>[id,e.descriptor]));this.paused=manager.paused;
    await manager.close({preserve:true});this.generation=manager.generation;
  }
}
module.exports={CaptureSession};
