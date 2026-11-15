namespace Rayme.Overlay;

public static class ApplicationSlots {
    public static readonly string[] All=["a","b","c","d","e"];
    public static bool Valid(string slot)=>All.Contains(slot);
    public static bool PairKey(string key)=>key.Length==3&&key[1]=='+'&&Valid(key[..1])&&Valid(key[2..])&&key[0]!=key[2];
    public static string Key(IEnumerable<string> slots)=>string.Join("+",slots);
}
public record CaptionGroup(string[] Slots) { public string Key=>ApplicationSlots.Key(Slots); }

// Presentation only: never sends a configuration command or changes source IDs.
// Slot order is significant (top/bottom); saved placements use that same key.
public sealed class CaptionLayout {
    readonly List<string[]> pairs=[];
    readonly Dictionary<string,string[]> orders=[];
    static string Unordered(IEnumerable<string> slots)=>ApplicationSlots.Key(slots.Order());
    public Dictionary<string,string[]> Orders=>orders.ToDictionary(p=>p.Key,p=>p.Value.ToArray());
    string[] active=[];
    public CaptionLayout(IEnumerable<string[]>? saved=null,IReadOnlyDictionary<string,string[]>? savedOrders=null){
        foreach(var p in savedOrders??new Dictionary<string,string[]>())if(p.Value is{Length:2}&&p.Value.All(ApplicationSlots.Valid)&&p.Value[0]!=p.Value[1]&&Unordered(p.Value)==p.Key)orders[p.Key]=p.Value.ToArray();
        var used=new HashSet<string>();
        foreach(var p in saved??new[]{new[]{"a","b"}})
            if(p is{Length:2}&&p.All(ApplicationSlots.Valid)&&p[0]!=p[1]&&!p.Any(used.Contains)){pairs.Add(p.ToArray());orders[Unordered(p)]=p.ToArray();foreach(var s in p)used.Add(s);}
    }
    public string[][] Pairings=>pairs.Select(p=>p.ToArray()).ToArray();
    public CaptionGroup[] Groups=>active.Contains("device")?[new(["device"])]:
        pairs.Where(p=>p.All(active.Contains)).Select(p=>new CaptionGroup(p.ToArray()))
        .Concat(active.Where(s=>!pairs.Any(p=>p.All(active.Contains)&&p.Contains(s))).Select(s=>new CaptionGroup([s]))).ToArray();
    public bool Detached=>active.Length>=2&&Groups.All(g=>g.Slots.Length==1);
    public static bool CanDetach(CaptionState state)=>TranscriptPresentation.Slots(state).Count(ApplicationSlots.Valid)>=2;
    public void Synchronize(CaptionState state){
        var next=TranscriptPresentation.Slots(state);
        // Match the established two-source removal/re-enable behavior, without
        // discarding other pair choices or any saved window arrangements.
        if(active.Contains("a")&&active.Contains("b")&&next.Count(ApplicationSlots.Valid)<2&&!next.Contains("device")){
            pairs.RemoveAll(p=>p.Contains("a")||p.Contains("b"));
            if(!pairs.Any(p=>p.Contains("a")||p.Contains("b")))pairs.Insert(0,["a","b"]);
        }
        active=next;
    }
    public CaptionGroup? GroupFor(string slot)=>Groups.FirstOrDefault(g=>g.Slots.Contains(slot));
    public string Profile(CaptionGroup group)=>group.Slots.Length==2?group.Key:pairs.Where(p=>p.Contains(group.Slots[0])).Select(ApplicationSlots.Key).FirstOrDefault()??group.Key;
    public string[] Targets(string slot)=>ApplicationSlots.All.Where(s=>active.Contains(s)&&s!=slot).ToArray();
    public bool Separate(string key){var group=Groups.FirstOrDefault(g=>g.Key==key);return group is{Slots.Length:2}&&pairs.RemoveAll(p=>ApplicationSlots.Key(p)==key)>0;}
    public bool Attach(string standalone,string target){
        var from=GroupFor(standalone);var to=GroupFor(target);
        if(from is not{Slots.Length:1}||to==null||from.Key==to.Key||!ApplicationSlots.Valid(standalone)||!ApplicationSlots.Valid(target))return false;
        if(to.Slots.Length==2){int index=pairs.FindIndex(p=>ApplicationSlots.Key(p)==to.Key);var replacement=to.Slots.ToArray();replacement[Array.IndexOf(replacement,target)]=standalone;pairs[index]=replacement;orders[Unordered(replacement)]=replacement.ToArray();}
        else{pairs.RemoveAll(p=>p.Contains(standalone)||p.Contains(target));var key=Unordered(new[]{target,standalone});var ordered=orders.GetValueOrDefault(key)??new[]{target,standalone}.Order().ToArray();pairs.Add(ordered.ToArray());orders[key]=ordered.ToArray();}
        return true;
    }
    public bool Detach(CaptionState state){Synchronize(state);var pair=Groups.FirstOrDefault(g=>g.Slots.Length==2);return pair!=null&&Separate(pair.Key);}
    public void Reattach(){if(active.Contains("a")&&active.Contains("b")){pairs.RemoveAll(p=>p.Contains("a")||p.Contains("b"));pairs.Insert(0,["a","b"]);}}
}

public record Availability(string Label,bool Healthy,bool Error,string Detail);
public static class SourceAvailability {
    public static Availability Read(SourceStatus? source,bool paused=false,bool connected=true){
        if(!connected)return new("Disconnected",false,true,"Caption connection unavailable");
        if(source==null)return new("Choose application",false,false,"");
        if(source.HasFailure||source.State=="unavailable")return new("Unavailable",false,true,source.Message);
        if(source.State=="recovering")return new("Recovering",false,true,source.Message);
        if(paused||source.State=="stopped")return new("Paused",false,false,"Capture paused");
        // Routine inference/capture changes never affect the availability label.
        // Persistent overload remains visible as a genuine warning, not healthy green.
        if(source.State=="delayed")return new("Delayed",false,true,source.Message);
        return new("Available",true,false,"Application capture available");
    }
}

public static class PairingPlacement {
    public static Preferences Attach(Preferences saved,string pairKey,string targetSlot,bool exchange,CaptionPlacement from,CaptionPlacement to){
        if(exchange)return saved.WithArrangement(pairKey,to).WithSource(targetSlot,from);
        return saved.ForArrangement(pairKey).Placement==null?saved.WithArrangement(pairKey,to):saved;
    }
}
