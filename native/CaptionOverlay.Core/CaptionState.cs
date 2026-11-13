using System.Text.Json;

namespace Rayme.Overlay;

public record Source(string Id, long Generation, string Name, string Identity);
public record Caption(Source Source, long Id, bool Final, string Text, double CapturedAt,long Revision=0,bool Unfinished=false) {
    public string Key => $"{Source.Id}:{Source.Generation}:{Id}:{Final}";
}
public record SourceStatus(Source Source, string State, string Message) {
    public string Label=>State switch{"waiting"=>"Listening — no speech","capturing"=>"Capture active","transcribing"=>"Transcribing","delayed"=>"Falling behind","unavailable"=>"Unavailable","recovering"=>"Recovering","stopped"=>"Stopped / paused",_=>State};
    public bool HasFailure=>State=="unavailable"||Message.Contains("Last failure:",StringComparison.OrdinalIgnoreCase)||Message.Contains("failed",StringComparison.OrdinalIgnoreCase);
}
public record AudioLevel(double Rms,double Dbfs,bool Low);
public record Choice(string Id, string Name) { public override string ToString() => Name; }

// Presentation state only. No audio, inference, process discovery or configuration authority.
public sealed class CaptionState {
    public List<Caption> History { get; } = [];
    public Dictionary<string, Caption> Partials { get; } = [];
    public Dictionary<string,AudioLevel> AudioLevels {get;}=[];
    public Dictionary<string, SourceStatus> Sources { get; } = [];
    readonly Dictionary<string, long> generations = [];
    readonly Dictionary<string, SegmentLedger> ledgers=[];
    SegmentLedger Ledger(string slot){if(!ledgers.TryGetValue(slot,out var book))ledgers[slot]=book=new();return book;}
    readonly HashSet<string> removed = [];
    public List<Choice> Apps { get; private set; } = [];
    public List<Choice> Devices { get; private set; } = [];
    public bool ProcessSupported { get; private set; }
    public bool Paused { get; private set; }
    public string Status { get; private set; } = "Connecting…";
    static string Str(JsonElement e, string name, int limit = 4000) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "")[..Math.Min(v.GetString()!.Length, limit)] : "";
    static bool Flag(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;
    static long Num(JsonElement e, string name) => e.TryGetProperty(name, out var v) && v.TryGetInt64(out long n) ? n : 0;
    static Source? ReadSource(JsonElement e) {
        if (!e.TryGetProperty("source", out var s) || s.ValueKind != JsonValueKind.Object) return null;
        var id = Str(s, "id", 16);
        return id is "a" or "b" or "device" ? new(id, Num(s, "generation"), HistoryPolicy.SourceName(Str(s, "name", HistoryPolicy.SourceNameLimit+1)), Str(s, "identity", 512)) : null;
    }
    static Caption ReadCaption(JsonElement e) => new(ReadSource(e) ?? new("legacy", 0, "Playback output", "legacy"), Num(e, "id"), Flag(e, "final"), HistoryPolicy.Limit(Str(e, "text",HistoryPolicy.TextLimit+1)), e.TryGetProperty("capturedAt", out var time) && time.TryGetDouble(out double at) && double.IsFinite(at) ? at : 0,Num(e,"revision"),Flag(e,"unfinished"));
    static List<Choice> Choices(JsonElement e, string name) => e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Take(512).Select(x => new Choice(Str(x, "id", 512), HistoryPolicy.SourceName(Str(x, "name", HistoryPolicy.SourceNameLimit+1)) + (name == "apps" && Num(x, "pid") > 0 ? $" (PID {Num(x, "pid")})" : ""))).ToList() : [];
    bool Obsolete(Source s) => Sources.TryGetValue(s.Id,out var active)&&active.Source.Generation==s.Generation&&active.Source.Identity!=s.Identity || generations.GetValueOrDefault(s.Id) > s.Generation || (removed.Contains(s.Id) && generations.GetValueOrDefault(s.Id) >= s.Generation);
    void Track(Source s) {
        if (generations.GetValueOrDefault(s.Id) < s.Generation) {
            if(Sources.TryGetValue(s.Id,out var previous)&&previous.Source.Identity==s.Identity&&Partials.TryGetValue(s.Id,out var draft))Retain(draft with{Final=false,Unfinished=true});
            Partials.Remove(s.Id);AudioLevels.Remove(s.Id); ledgers.Remove(s.Id);
            if (Sources.TryGetValue(s.Id, out var old)) { if(old.Source.Identity != s.Identity){History.RemoveAll(x => x.Source.Id == s.Id);Sources.Remove(s.Id);}else Sources[s.Id]=old with{Source=s}; }
        }
        generations[s.Id] = s.Generation; removed.Remove(s.Id);
        if(!Sources.ContainsKey(s.Id))Sources[s.Id]=new(s,"waiting","Waiting for audio");
    }
    void Trim() { var ordered=HistoryPolicy.Trim(History);History.Clear();History.AddRange(ordered); }
    void Retain(Caption caption){History.RemoveAll(c=>CaptionPresentation.RowKey(c)==CaptionPresentation.RowKey(caption));History.Add(caption);Trim();}
    public void Apply(string json) { using var doc = JsonDocument.Parse(json); Apply(doc.RootElement); }
    void Apply(JsonElement e) {
        string type = Str(e, "type", 40);
        if (type == "snapshot") {
            History.Clear(); Partials.Clear(); Sources.Clear();AudioLevels.Clear(); generations.Clear(); ledgers.Clear(); removed.Clear();
            Apps = Choices(e, "apps"); Devices = Choices(e, "devices"); ProcessSupported = Flag(e, "processSupported"); Paused = Flag(e, "paused"); Status = Str(e, "status");
            if (e.TryGetProperty("sources", out var sources)) foreach (var item in sources.EnumerateObject()) Apply(item.Value);
            if (e.TryGetProperty("history", out var history)) foreach (var item in history.EnumerateArray()) {
                var c = ReadCaption(item);
                // Old finalized conversation may survive capture resets, but never a replaced application.
                if (c.Source.Id != "legacy" && (!Sources.TryGetValue(c.Source.Id, out var active) || active.Source.Identity != c.Source.Identity || c.Source.Generation > active.Source.Generation)) continue;
                if (!History.Any(x => x.Key == c.Key)) History.Add(c);
                if (!Obsolete(c.Source)&&c.Final) Ledger(c.Source.Id).Mark(c.Id,0,closed:true,cleared:true,final:true);
            }
            Trim();
            if(e.TryGetProperty("epochs",out var epochs))foreach(var item in epochs.EnumerateObject()){
                var stamp=item.Value;var origin=ReadSource(stamp);if(origin!=null&&!Flag(stamp,"removed")&&!Obsolete(origin))Track(origin);if(origin!=null&&Flag(stamp,"removed")&&(!generations.TryGetValue(origin.Id,out var current)||current<=origin.Generation))Apply(JsonSerializer.SerializeToElement(new{type="source-removed",source=new{id=origin.Id,generation=origin.Generation,name=origin.Name,identity=origin.Identity}}));
            }
            if(e.TryGetProperty("segments",out var segments))foreach(var item in segments.EnumerateObject())if(item.Name=="legacy"||generations.GetValueOrDefault(item.Name)==Num(item.Value,"generation"))Ledger(item.Name).Load(item.Value);
            void RestorePartial(JsonElement item){var c=ReadCaption(item);if(Obsolete(c.Source)||c.Source.Id!="legacy"&&(!Sources.TryGetValue(c.Source.Id,out var active)||active.Source.Generation!=c.Source.Generation||active.Source.Identity!=c.Source.Identity))return;var book=Ledger(c.Source.Id);long rev=book.Revision(c.Id,item);if(book.Allows(c.Id,rev,snapshot:true)&&c.Text.Length>0){Partials[c.Source.Id]=c with{Revision=rev};book.Mark(c.Id,rev);}}
            if (e.TryGetProperty("partials", out var partials)) foreach (var p in partials.EnumerateObject()) RestorePartial(p.Value);
            if (e.TryGetProperty("partial", out var partial) && partial.ValueKind == JsonValueKind.Object) RestorePartial(partial);
            if(e.TryGetProperty("audioLevels",out var levels))foreach(var level in levels.EnumerateObject())Apply(level.Value);
            return;
        }
        if (type == "applications") { Apps = Choices(e, "apps"); ProcessSupported = Flag(e, "supported"); }
        if (type == "devices") Devices = Choices(e, "devices");
        if (type == "status") Status = Str(e, "message");
        if (type == "paused") Paused = Flag(e, "paused");
        if (type == "cleared") { History.Clear(); Partials.Clear(); }
        var source = ReadSource(e);
        if (source != null && Obsolete(source)) return;
        if(type=="audio-level"&&source!=null){if(Sources.TryGetValue(source.Id,out var active)&&active.Source.Generation==source.Generation&&active.Source.Identity==source.Identity)AudioLevels[source.Id]=new(Num(e,"rms"),Num(e,"dbfs"),Flag(e,"low"));return;}
        if (type is "source-status" or "reset" && source != null) {
            Track(source);
            if(type=="source-status")Sources[source.Id]=new(source,Str(e,"state",40),Str(e,"message"));
            else{
                AudioLevels.Remove(source.Id);
                if(Partials.TryGetValue(source.Id,out var pending))Ledger(source.Id).Mark(pending.Id,pending.Revision,closed:true,cleared:true);
                Partials.Remove(source.Id);var previous=Sources.GetValueOrDefault(source.Id);
                Sources[source.Id]=new(source,previous?.State??"waiting",previous?.Message??"Waiting for audio");
            }
        }
        if (type == "reset" && source == null) {if(Partials.TryGetValue("legacy",out var p))Ledger("legacy").Mark(p.Id,p.Revision,closed:true,cleared:true);Partials.Remove("legacy");}
        if (type == "source-removed" && source != null) {
            generations[source.Id] = source.Generation; removed.Add(source.Id); Sources.Remove(source.Id);AudioLevels.Remove(source.Id); Partials.Remove(source.Id); ledgers.Remove(source.Id); History.RemoveAll(x => x.Source.Id == source.Id);
        }
        if(type=="segment-retired"){
            if(source!=null)Track(source);
            string slot=source?.Id??"legacy";var book=Ledger(slot);long id=Num(e,"id"),revision=book.Revision(id,e);bool terminal=!e.TryGetProperty("terminal",out var end)||end.ValueKind!=JsonValueKind.False;
            book.Mark(id,revision,closed:terminal,cleared:true);
            if(terminal&&!Flag(e,"retain"))History.RemoveAll(c=>c.Unfinished&&c.Source.Id==slot&&c.Source.Generation==(source?.Generation??0)&&c.Id==id);
            if(Partials.TryGetValue(slot,out var pending)&&(pending.Id<id||pending.Id==id&&(terminal||pending.Revision<=revision))){if(Flag(e,"retain"))Retain(pending with{Final=false,Unfinished=true});Partials.Remove(slot);}
        }
        if (type != "caption") return;
        var caption = ReadCaption(e); var slotId = caption.Source.Id;
        if (source != null) { Track(source); if (!Sources.ContainsKey(slotId)) Sources[slotId] = new(source, "capturing", ""); }
        var ledger=Ledger(slotId);long rev=ledger.Revision(caption.Id,e);caption=caption with{Revision=rev};
        if(!ledger.Allows(caption.Id,rev,caption.Final))return;
        if(caption.Text.Length==0){ledger.Mark(caption.Id,rev,closed:caption.Final,cleared:true);if(Partials.TryGetValue(slotId,out var old)&&(old.Id<caption.Id||old.Id==caption.Id&&old.Revision<=rev))Partials.Remove(slotId);return;}
        if(Partials.TryGetValue(slotId,out var preceding)&&preceding.Id<caption.Id)Retain(preceding with{Final=false,Unfinished=true});
        ledger.Mark(caption.Id,rev,closed:caption.Final,cleared:caption.Final,final:caption.Final);
        if (caption.Final) {
            if (!Partials.TryGetValue(slotId, out var pending) || pending.Id <= caption.Id) Partials.Remove(slotId);
            Retain(caption);
        } else if (!Partials.TryGetValue(slotId, out var pending) || pending.Id <= caption.Id) {History.RemoveAll(c=>CaptionPresentation.RowKey(c)==CaptionPresentation.RowKey(caption));Partials[slotId] = caption;}
    }
}
