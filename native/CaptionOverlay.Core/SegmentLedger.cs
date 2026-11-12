using System.Text.Json;

namespace Rayme.Overlay;

internal sealed class SegmentLedger {
    record Entry(long Revision,bool Closed,bool Cleared);
    readonly Dictionary<long,Entry> entries=[];
    long floor,finalized,latest;
    public long Revision(long id,JsonElement e)=>e.TryGetProperty("revision",out var value)&&value.TryGetInt64(out long r)&&r>=0?r:entries.GetValueOrDefault(id)?.Revision+1??1;
    public bool Allows(long id,long revision,bool final=false,bool snapshot=false){
        if(id<=floor||!final&&(id<=finalized||id<latest))return false;
        if(!entries.TryGetValue(id,out var old))return true;
        if(old.Closed)return false;
        return final?revision>=old.Revision:revision>old.Revision||snapshot&&revision==old.Revision&&!old.Cleared;
    }
    public void Mark(long id,long revision,bool closed=false,bool cleared=false,bool final=false){
        if(id<=floor||entries.TryGetValue(id,out var old)&&(old.Closed||!closed&&old.Revision>revision))return;
        latest=Math.Max(latest,id);entries[id]=new(Math.Max(revision,old?.Revision??0),closed,cleared);if(final||closed)finalized=Math.Max(finalized,id);
        while(entries.Count>128){long first=entries.Keys.Min();entries.Remove(first);floor=Math.Max(floor,first);}
    }
    public void Load(JsonElement metadata){
        entries.Clear();floor=metadata.TryGetProperty("floor",out var f)?f.GetInt64():0;finalized=metadata.TryGetProperty("finalized",out var n)?n.GetInt64():0;latest=metadata.TryGetProperty("latest",out var l)?l.GetInt64():finalized;
        if(metadata.TryGetProperty("entries",out var list))foreach(var e in list.EnumerateArray().TakeLast(128))entries[e.GetProperty("id").GetInt64()]=new(e.GetProperty("revision").GetInt64(),e.GetProperty("closed").GetBoolean(),e.GetProperty("cleared").GetBoolean());
    }
}
