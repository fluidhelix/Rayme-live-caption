using System.Text.Json;
using Rayme.Overlay;

static class Program {
    static int checks;
    static void Check(bool ok, string why) { if (!ok) throw new Exception(why); checks++; }
    static string Event(string type, Source? source = null, long id = 1, bool final = false, string text = "words", long capturedAt = 1) => JsonSerializer.Serialize(new { type, source = source == null ? null : new { id = source.Id, generation = source.Generation, name = source.Name, identity = source.Identity }, id, final, text, capturedAt });
    static readonly Source A = new("a",1,"Browser A","10:100"), B = new("b",2,"Conversation B","20:200");
    static async Task<int> Main(string[] args) {
        try { if (args.Contains("--session")) await SessionTest(); else UnitTests(); Console.WriteLine($"PASS: {checks} native overlay assertions" ); return 0; }
        catch (Exception ex) { Console.Error.WriteLine("FAIL: " + ex.Message); return 1; }
    }
    static void UnitTests() {
        ApprovedDesignTests();
        ReadingTests();
        LifecycleTests();
        UsabilityTests();
        var s = new CaptionState(); s.Apply(Event("reset",A)); s.Apply(Event("reset",B));
        s.Apply(Event("caption",A,text:"[laughter]")); s.Apply(Event("caption",B,text:"*coughing*"));
        Check(s.Partials.Count == 2,"Independent partials");
        s.Apply(Event("caption",B,final:true,text:"*sighing*",capturedAt:20)); s.Apply(Event("caption",A,final:true,text:"<b>literal</b> [laughter]",capturedAt:10));
        Check(s.Partials.Count == 0 && s.History.Count == 2,"Partial-to-final replacement");
        Check(s.History[0].Source == A && s.History[1].Source == B,"Interleaved capture ordering and source attribution");
        Check(s.History[0].Text == "<b>literal</b> [laughter]" && s.History[1].Text == "*sighing*","Safe literal annotations");
        s.Apply(Event("caption",A,final:true)); s.Apply(Event("caption",A)); Check(s.History.Count == 2 && s.Partials.Count == 0,"Duplicate final and late partial suppressed");
        s.Apply(Event("caption",B,3,text:"new partial")); s.Apply(Event("caption",B,2,text:"older partial")); Check(s.Partials["b"].Id == 3,"Out-of-order partial cannot replace newer partial");
        s.Apply(Event("caption",B,2,true)); Check(s.Partials["b"].Id == 3,"Older final cannot erase newer partial");
        s.Apply(Event("source-removed",A)); s.Apply(Event("caption",A,2,true)); Check(s.History.All(x => x.Source.Id != "a") && !s.Partials.ContainsKey("a"),"Removal tombstone rejects stale events");
        var replacement = A with { Generation = 3, Identity = "11:101", Name = "New application" };
        s.Apply(Event("reset",replacement)); s.Apply(Event("caption",A,3,true)); Check(s.History.All(x => x.Source.Id != "a"),"Old generation invalidated");
        s.Apply(Event("caption",replacement,1,true)); Check(s.History.Any(x => x.Source == replacement && x.Id == 1),"Replacement segment IDs restart safely");
        s.Apply("""{"type":"paused","paused":true}"""); Check(s.Paused,"Pause state"); s.Apply("""{"type":"paused","paused":false}"""); Check(!s.Paused,"Resume state");
        s.Apply("""{"type":"cleared"}"""); Check(s.History.Count == 0 && s.Partials.Count == 0,"Clear removes all displayed conversation");
        for (int i=2;i<=250;i++) s.Apply(Event("caption",replacement,i,true,capturedAt:i)); Check(s.History.Count == 200 && s.History[0].Id == 51,"History bounded to 200 rows per source");
        string snapshot = JsonSerializer.Serialize(new { type = "snapshot", status = "Ready", paused = false, history = new[] { JsonSerializer.Deserialize<JsonElement>(Event("caption",replacement,50,true)), JsonSerializer.Deserialize<JsonElement>(Event("caption",A,1,true)) }, sources = new { a = JsonSerializer.Deserialize<JsonElement>(Event("source-status",replacement)) }, partials = new { }, devices = new object[0], apps = new object[0] });
        s.Apply(snapshot); s.Apply(snapshot); Check(s.History.Count == 1 && s.History[0].Source == replacement,"Authoritative reconnect snapshot replaces state and drops replaced identities");
        s.Apply("""{"type":"snapshot","history":[],"sources":{},"partials":{},"devices":[],"apps":[],"paused":true}"""); Check(s.History.Count == 0 && s.Paused,"Snapshot after clear never resurrects captions");
        var p = Preferences.Parse("{broken"); Check(p == new Preferences(),"Corrupt settings use safe defaults");
        p = Preferences.Parse("""{"Version":1,"FontSize":200,"BackgroundOpacity":0,"ColorA":"javascript:bad","Placement":{"X":-1920,"Y":-1080,"Width":640,"Height":360}}""");
        Check(p.FontSize == 60 && p.BackgroundOpacity == .70 && p.ColorA == "#80DFFF","Settings bounds and color validation");
        static double Luminance(double[] rgb) { var c=rgb.Select(v=>v/255).Select(v=>v<=.04045?v/12.92:Math.Pow((v+.055)/1.055,2.4)).ToArray(); return .2126*c[0]+.7152*c[1]+.0722*c[2]; }
        double background=Luminance(new double[]{15,23,38}.Select(v=>Math.Floor(p.BackgroundOpacity*255)/255*v+(1-Math.Floor(p.BackgroundOpacity*255)/255)*255).ToArray());
        Check((Luminance([224,231,242])+.05)/(background+.05)>=4.5,"Opacity floor keeps small source labels readable over a white desktop");
        Check(p.Placement?.X == -1920 && p.Placement.Y == -1080,"Negative coordinates survive settings serialization");
        Check(Preferences.Parse("{\"Version\":99}") == new Preferences(),"Unknown settings schema recovers");
        var legacy=new Preferences{FontSize=34,BackgroundOpacity=.82,ColorA="#ABCDEF",ColorB="#BADA55",Locked=true,Topmost=false,Monitor="portrait",Placement=new(-1080,-1440,640,360)};
        Check(Preferences.Parse(JsonSerializer.Serialize(legacy))==legacy,"Existing version-1 custom appearance and negative-coordinate placement survive the redesign unchanged");
        Check((new Preferences { FontSize = double.NaN, Placement = new(double.PositiveInfinity,0,1,1) }).Validate().Placement == null,"Invalid floating-point placement rejected");
        var dir = Path.Combine(Path.GetTempPath(),"rayme-overlay-"+Guid.NewGuid()); var file = Path.Combine(dir,"settings.json");
        try { Check(Preferences.Load(file) == new Preferences(),"Missing settings recover"); Check(p.Save(file) && Preferences.Load(file) == p,"Settings atomic round-trip"); var json = File.ReadAllText(file); Check(!json.Contains("token") && !json.Contains("caption",StringComparison.OrdinalIgnoreCase),"Settings contain no credentials or transcripts"); File.WriteAllText(file,"{bad"); Check(Preferences.Load(file) == new Preferences(),"Corrupt file recovers"); }
        finally { if (Directory.Exists(dir)) Directory.Delete(dir,true); }
        Display[] monitors = [new("primary",new(0,0,1920,1040)),new("upper",new(0,-1440,2560,1440),1.5),new("portrait",new(-1080,-500,1080,1920))];
        var b = Preferences.Recover(new(400,-1300,700,500),monitors,"upper"); Check(b.Y == -1300 && b.X == 400,"Restore upper monitor with negative Y");
        b = Preferences.Recover(new(-1000,-400,700,1000),monitors,"portrait"); Check(b.X == -1000 && b.Y == -400,"Portrait / negative X monitor preserved");
        b = Preferences.Recover(new(400,-1300,700,500),monitors[..1],"upper"); Check(b.X >= 0 && b.Y >= 0 && b.X+b.Width <=1920 && b.Y+b.Height<=1040,"Disconnected monitor recovers fully into visible work area");
        b = Preferences.Recover(new(-99999,99999,5,5),monitors,"upper"); Check(b.Width == 450 && b.Height == 270 && b.X >= 0 && b.Y < 0,"DPI-aware minimum bounds and offscreen recovery");
        b = Preferences.Recover(new(0,0,9000,9000),monitors[..1]); Check(b.Width == 1920 && b.Height == 1040,"Oversized window fits available work area");
        b = Preferences.Recover(new(double.NaN,0,500,300),monitors); Check(double.IsFinite(b.X) && double.IsFinite(b.Y),"Recovery handles nonfinite direct inputs");
        var sameTime = new CaptionState();
        sameTime.Apply(Event("caption",A,1,true,capturedAt:5)); sameTime.Apply(Event("caption",B,1,true,capturedAt:5));
        Check(sameTime.History[0].Source == A && sameTime.History[1].Source == B,"Equal capture timestamps retain arrival order");
        sameTime.Apply("""{"type":"caption","source":{"id":"a","generation":1,"name":"Browser A","identity":"10:100"},"id":2,"final":true,"text":"fractional timestamp","capturedAt":4.5}""");
        Check(sameTime.History[0].CapturedAt == 4.5,"Sub-millisecond capture timestamps preserve intended ordering");
        var partialRow=new Caption(A,7,false,"[laughter]",10);
        Check(CaptionPresentation.RowKey(partialRow)==CaptionPresentation.RowKey(partialRow with{Final=true}),"Partial/final presentation retains a stable row identity");
        Check(CaptionPresentation.RowKey(partialRow)!=CaptionPresentation.RowKey(partialRow with{Source=A with{Generation=3}}),"Replaced generations never reuse stale visual rows");
        Check(partialRow.Key!=(partialRow with{Final=true}).Key,"Presentation identity does not change protocol deduplication semantics");
        foreach(var (icon,display) in new[]{
            (new Bounds(1800,1040,24,24),monitors[0]),
            (new Bounds(200,-1470,24,24),monitors[1]),
            (new Bounds(-1110,400,24,24),monitors[2]),
            (new Bounds(1920,400,24,24),monitors[0]),
            (new Bounds(-2000,-3000,24,24),monitors[1])}) {
            var placed=FlyoutPlacement.Place(icon,286*display.Scale,360*display.Scale,display);var work=display.Work;
            Check(placed.X>=work.X&&placed.Y>=work.Y&&placed.X+placed.Width<=work.X+work.Width&&placed.Y+placed.Height<=work.Y+work.Height,"Tray flyout clamps to visible work area for bottom/top/left/right/offscreen anchors");
        }
        var bottom=FlyoutPlacement.Place(new(1800,1040,24,24),286,360,monitors[0]);Check(bottom.Y+bottom.Height<1040,"Bottom taskbar opens flyout above the icon");
        var top=FlyoutPlacement.Place(new(200,-1470,24,24),429,540,monitors[1]);Check(top.Y>=-1440,"Upper scaled monitor taskbar opens into its visible work area");
        foreach (string url in new[] { "https://127.0.0.1/?token="+new string('a',48), "http://localhost/?token="+new string('a',48), "http://example.com/?token="+new string('a',48), "http://127.0.0.1/?token=secret", "http://127.0.0.1/path?token="+new string('a',48) }) {
            bool rejected = false; try { _ = new SessionClient(url); } catch (ArgumentException) { rejected = true; } Check(rejected,"Only exact authenticated loopback sessions accepted");
        }
    }
    static void ApprovedDesignTests(){
        var s=new CaptionState();var layout=new CaptionLayout();Check(TranscriptPresentation.Slots(s).Length==0&&!layout.Detach(s),"No disabled-source caption panels and no detach with no sources");
        s.Apply(Event("reset",B));Check(TranscriptPresentation.Slots(s).SequenceEqual(new[]{"b"})&&!CaptionLayout.CanDetach(s),"B alone fills the window and cannot detach");
        s.Apply(Event("reset",A));Check(TranscriptPresentation.Slots(s).SequenceEqual(new[]{"a","b"})&&!layout.Detached,"Enabling both sources starts combined with deterministic A/B order");
        Check(layout.Detach(s)&&layout.Detached,"Explicit detach requires both enabled sources");s.Apply("""{"type":"paused","paused":true}""");layout.Synchronize(s);Check(layout.Detached,"Pause does not change the chosen layout");
        s.Apply(Event("source-removed",B));layout.Synchronize(s);Check(!layout.Detached&&TranscriptPresentation.Slots(s).SequenceEqual(new[]{"a"}),"Source removal automatically reattaches the remaining source");
        s.Apply(Event("reset",B with{Generation=3}));layout.Synchronize(s);Check(!layout.Detached,"Re-enabling the second source does not detach automatically");
        Check(layout.Detach(s),"Detaching again is explicit");layout.Reattach();Check(!layout.Detached,"Reattach returns to combined mode");
        var old=new Preferences{ColorA="#A1B2C3",FontSize=41,BackgroundOpacity=.83,Placement=new(-1200,-900,640,360)};
        var migrated=Preferences.Parse(JsonSerializer.Serialize(old));Check(migrated.ColorA==old.ColorA&&migrated.FontSize==41&&migrated.BackgroundOpacity==.83&&migrated.Placement==old.Placement,"Existing appearance and combined placement survive additive settings fields");
        var distinct=migrated.WithSource("a",new(new(-1000,-500,500,220),"upper",true)).WithSource("b",new(new(200,-1200,450,280),"portrait",false));
        var loaded=Preferences.Parse(JsonSerializer.Serialize(distinct));Check(loaded.DetachedA==distinct.DetachedA&&loaded.DetachedB==distinct.DetachedB&&loaded.DetachedA.Locked&&!loaded.DetachedB.Locked,"Independent detached placements and locks round-trip without persisting layout mode");
        Check(!(JsonSerializer.Serialize(loaded).Contains("token")||JsonSerializer.Serialize(loaded).Contains("transcript")),"Layout preferences contain no credentials or transcript");
        Check((loaded with{DetachedA=new(new(double.NaN,0,1,1),new string('x',200))}).Validate().DetachedA==new CaptionPlacement(),"Invalid detached placement and monitor recover safely");
        foreach(string color in new[]{"#80DFFF","#FFD68A","#CBB9FF","#A3D9B1","#000000","#FFFFFF"}){var(h,sat,v)=SourceColor.Hsv(color);Check(SourceColor.FromHsv(h,sat,v)==color,"HSV and RGB preserve exact source color "+color);}
        Check(SourceColor.Default("a")=="#80DFFF"&&SourceColor.Default("b")=="#FFD68A"&&SourceColor.Default("device")=="#CBB9FF","Restore default uses original Rayme values");
        Check(SourceColor.TryHex("#a3d9b1",out var hex)&&hex=="#A3D9B1"&&!SourceColor.TryHex("#GGGGGG",out _)&&!SourceColor.TryHex("javascript:bad",out _),"Custom color validation accepts six hex digits only");
        Check(SourceColor.ContrastOnBlack("#000000")==1&&SourceColor.ContrastOnBlack("#FFFFFF")>20,"Color-picker contrast warning uses actual contrast");
        Check(TextPosition.Map("hello old ending","hello corrected ending",3)==3,"Reading anchors in unchanged prefix remain stable");
        Check(TextPosition.Map("hello old ending","hello corrected ending",12)==18,"Reading anchors in unchanged suffix follow final correction");
        // Reproduce the removed append-only rule: Whisper can revise the prefix
        // and add words, which still requires reveal when its tail is obscured.
        string draft="early words",corrected="Corrected earlier words continue";
        Check(!corrected.StartsWith(draft,StringComparison.Ordinal)&&new ScrollFollow().NeedsReveal(220,250,200),"A non-prefix correction must not inherit the old append-only follow gate");

    }
    static void ReadingTests(){
        var longSource=A with{Name=new string('界',199)+"😀"+string.Concat(Enumerable.Repeat("超長😀",500)),Identity="preserved:😀:10:100"};
        var longState=new CaptionState();for(int id=1;id<=210;id++)longState.Apply(Event("caption",longSource,id,true,new string('x',400),id));
        int expected=HistoryPolicy.Bytes/HistoryPolicy.Cost(new(longSource,1,true,new string('x',400),1));var liveIds=longState.History.Select(c=>c.Id).ToArray();
        Check(longState.History.Count==expected&&expected<200,"Long Unicode source names use normalized metadata in the native byte budget");
        Check(longState.History.All(c=>c.Source.Name==new string('界',199)&&c.Source.Identity==longSource.Identity),"Source-name truncation preserves surrogate pairs and source identities");
        Check(HistoryPolicy.SourceName(new string('界',198)+"😀tail")==new string('界',198)+"😀","Source-name boundary retains a complete surrogate pair when it fits");
        var rawSource=new{id=longSource.Id,generation=longSource.Generation,name=longSource.Name,identity=longSource.Identity};
        longState.Apply(JsonSerializer.Serialize(new{type="snapshot",sources=new{a=new{type="source-status",source=rawSource,state="waiting"}},history=longState.History.Select(c=>new{source=rawSource,id=c.Id,text=c.Text,final=true,capturedAt=c.CapturedAt}).ToArray(),partials=new{}}));
        Check(longState.History.Select(c=>c.Id).SequenceEqual(liveIds),"Long-name reconnect normalization cannot discard additional live native history");
        Check(HistoryPolicy.Limit(new string('x',3999)+"😀")==new string('x',3999)&&HistoryPolicy.Limit("text 😀")=="text 😀","Native text bound never splits a Unicode surrogate pair");
        var s=new CaptionState();var device=new Source("device",3,"Playback","default");
        foreach(var source in new[]{A,B,device})for(int id=1;id<=235;id++)s.Apply(Event("caption",source,id,true,capturedAt:id));
        foreach(var source in new[]{A,B,device}){var h=s.History.Where(c=>c.Source.Id==source.Id).ToArray();Check(h.Length==200&&h[0].Id==36,"Each source independently retains 200 segments");}
        var before=s.History.ToArray();for(int i=0;i<1000;i++)s.Apply(JsonSerializer.Serialize(new{type="source-status",source=new{id=A.Id,generation=A.Generation,name=A.Name,identity=A.Identity},state="waiting",message="Silence"}));
        Check(s.History.SequenceEqual(before),"Prolonged silence/status changes cannot expire finalized native history");
        for(int id=236;id<=260;id++)s.Apply(Event("caption",A,id,true,new string('x',4000),id));
        Check(s.History.Count(c=>c.Source.Id=="a")==16&&s.History.Where(c=>c.Source.Id=="a").Sum(c=>c.Text.Length)<=HistoryPolicy.Characters,"Native text-character budget bounds long segments");
        for(int id=261;id<=285;id++)s.Apply(Event("caption",A,id,true,new string('\u0001',4000),id));
        Check(s.History.Count(c=>c.Source.Id=="a")==5&&s.History.Where(c=>c.Source.Id=="a").Sum(HistoryPolicy.Cost)<=HistoryPolicy.Bytes,"Native encoded-byte budget matches shared escaped-text accounting");
        Check(s.History.Count(c=>c.Source.Id=="b")==200&&s.History.Count(c=>c.Source.Id=="device")==200,"A's cleanup cannot evict B or device history");
        s=new CaptionState();
        string Retire(long id,long revision,bool terminal=false)=>JsonSerializer.Serialize(new{type="segment-retired",source=new{id=A.Id,generation=A.Generation,name=A.Name,identity=A.Identity},id,revision,terminal,retain=true});
        var capped=new CaptionState();for(int id=1;id<=200;id++)capped.Apply(Event("caption",A,id,true,capturedAt:id));capped.Apply(Event("caption",A,201,false,"Useful draft at capacity",201));
        var liveKey=TranscriptPresentation.Segments(capped,"a").Last().Key;capped.Apply(Retire(201,1));
        Check(capped.History.Count==200&&capped.History[0].Id==2&&capped.History[^1].Unfinished,"At-capacity retirement consumes one retained slot and prunes the oldest record");
        Check(TranscriptPresentation.Segments(capped,"a").Last().Key==liveKey,"Budget-bound retirement retains the same draft's visual identity");
        s.Apply(Event("caption",A,1,false,"Unverified words"));s.Apply(Retire(1,1));
        Check(s.Partials.Count==0&&s.History.Single() is {Final:false,Unfinished:true,Text:"Unverified words"},"Retirement retains the actual draft without declaring it final");
        var segment=TranscriptPresentation.Segments(s,"a").Single();Check(segment.Unfinished&&!segment.Final&&segment.Text=="Unverified words","Retained draft receives no invented final punctuation");
        Check(TranscriptPresentation.Build(s,"a") is{Live:false,Unfinished:true},"DRAFT presentation is distinct from live and verified final speech");
        Check(TranscriptPresentation.Build(s,"a").FinalText.Length==0,"Retained unfinished text is never exposed as verified FinalText");
        s.Apply("""{"type":"caption","source":{"id":"a","generation":1,"name":"Browser A","identity":"10:100"},"id":1,"revision":1,"text":"old","final":false}""");Check(s.Partials.Count==0,"Old expired revision cannot revive live text");
        s.Apply(Event("caption",A,1,true,"Correct final"));Check(s.History.Count==1&&s.History[0].Final&&!s.History[0].Unfinished&&CaptionPresentation.RowKey(s.History[0])==segment.Key,"Fresh final replaces the retained draft under the same visual identity");
        s.Apply(Event("caption",A,2,false,"Clear me"));s.Apply("""{"type":"cleared"}""");s.Apply(Retire(2,2));Check(s.History.Count==0&&s.Partials.Count==0,"Late expiration cannot reconstruct explicitly cleared native text");
        s.Apply(Event("caption",A,3,false,"Snapshot draft"));s.Apply(Retire(3,3));
        var origin=new{id=A.Id,generation=A.Generation,name=A.Name,identity=A.Identity};
        string snapshot=JsonSerializer.Serialize(new{type="snapshot",sources=new{a=new{type="source-status",source=origin,state="delayed",message="Recognition unfinished"}},history=new[]{new{source=origin,id=3,revision=1,final=false,unfinished=true,text="Snapshot draft",capturedAt=3}},partials=new{},devices=Array.Empty<object>(),apps=Array.Empty<object>()});
        s.Apply(snapshot);Check(s.History.Single().Unfinished&&!s.History[0].Final,"Reconnect preserves unfinished classification");s.Apply(Event("caption",A,3,true,"Recovered final"));Check(s.History.Count==1&&s.History[0].Final,"Snapshot draft does not falsely close a segment awaiting a fresh final");
        s.Apply(Event("caption",A,4,false,"Draft before restart"));s.Apply(Event("reset",A with{Generation=4}));Check(s.History.Count==2&&s.History[1].Unfinished&&s.Partials.Count==0,"Same-application capture reset retains the old draft as unfinished history");s.Apply(Event("caption",A,4,true,"Old generation"));Check(s.History[1].Text=="Draft before restart","Old generation cannot replace retained history or revive live speech");
        var follow=new ScrollFollow();var other=new ScrollFollow();
        foreach(string change in new[]{"prefix extension","non-prefix revision with new words","shorter correction","partial-to-final"}){Check(follow.NeedsReveal(220,250,200),"Following reveals an obscured latest line for "+change);Check(!follow.NeedsReveal(170,195,200),"Already visible latest line needs no scroll for "+change);}
        long ticket=follow.Revision;follow.BeginNavigation();Check(!follow.Following&&follow.Revision>ticket,"Explicit reader input invalidates scheduled following before scroll events arrive");follow.ReaderMoved(0,500);Check(!follow.NeedsReveal(220,250,200)&&other.Following,"Manual rereading blocks follow independently of the other source");
        follow.ReaderMoved(500,500);Check(follow.NeedsReveal(220,250,200),"Manual return to the bottom resumes following");
        follow.ReaderMoved(500,500,selecting:true);Check(!follow.NeedsReveal(220,250,200),"An active user selection prevents near-bottom follow from rearming");
        follow.ReaderMoved(500,500);Check(follow.Following,"Explicit non-selection navigation back to the bottom resumes following");
        Check(follow.NeedsReveal(-3.8166666667,25.4466666667,209),"A latest line clipped by a committed anchor scroll still requires reveal");
        follow.ReaderMoved(1673.4,1721.4,movingAway:true);Check(!follow.Following,"A 48-DIP upward wheel step stays in rereading mode inside the unchanged 60-DIP band");
        follow.ReaderMoved(1711.4,1721.4);Check(follow.Following,"Downward reader return inside the existing near-bottom band resumes follow");
        Check(!follow.NeedsReveal(double.NaN,250,200)&&!follow.NeedsReveal(0,0,0),"Unmeasured layout cannot request a spurious scroll");
    }
    static void UsabilityTests(){
        foreach(var (input,expected) in new[]{("Hello","Hello."),("Why?","Why?"),("Yes!","Yes!"),("Already.","Already."),("[laughter]","[laughter]"),("*sighing*","*sighing*"),("  More words  ","More words."),("He said “yes”","He said “yes.”"),("你好。","你好。"),("",""),("Wait…","Wait…"),("Continuing,","Continuing,"),("Listen:","Listen:"),("unfinished —","unfinished —"),("Hello [laughter]","Hello. [laughter]"),("Why? *sighing*","Why? *sighing*"),("[laughter] *sighing*","[laughter] *sighing*")})Check(TranscriptPresentation.Finish(input)==expected,"Natural final punctuation: "+input);
        var state=new CaptionState();state.Apply(Event("reset",A));
        Check(TranscriptPresentation.Slots(state).SequenceEqual(new[]{"a"}),"Only enabled A occupies a single full panel");
        Check(TranscriptPresentation.Build(state,"a").Label==A.Name,"Caption title contains the actual application name only");
        state.Apply(Event("caption",A,1,text:"First draft"));Check(TranscriptPresentation.Build(state,"a").PartialText=="First draft","Partial occupies the live tail of its own panel");
        state.Apply(Event("caption",A,1,true,"First sentence"));state.Apply(Event("caption",A,2,text:"Second draft"));
        var text=TranscriptPresentation.Build(state,"a");Check(text.FinalText=="First sentence."&&text.PartialText=="Second draft","Final text and current partial are continuous, independent runs without duplication");
        state.Apply(Event("caption",A,2,true,"Second sentence?"));state.Apply(Event("reset",A with{Generation=3}));state.Apply("""{"type":"paused","paused":true}""");
        Check(TranscriptPresentation.Build(state,"a").FinalText=="First sentence. Second sentence?"&&!TranscriptPresentation.Build(state,"a").Live,"Silence, reset and pause preserve naturally joined finalized history");
        Check(TranscriptPresentation.Build(state,"b").FinalText.Length==0,"Other application transcript remains independent");
        state.Apply(Event("source-removed",A with{Generation=3}));var device=new Source("device",4,"Default output","default");state.Apply(Event("reset",device));
        Check(TranscriptPresentation.Slots(state).SequenceEqual(new[]{"device"}),"Whole-device fallback occupies one full panel");
        var a=new ScrollFollow();var b=new ScrollFollow();a.ReaderMoved(20,500);b.ReaderMoved(450,500);Check(!a.Following&&b.Following,"Each transcript maintains independent follow intent");long version=a.Revision;a.ReaderMoved(450,500);Check(a.Revision>version,"Reader navigation invalidates pending follow operations");Check(a.Following,"Near-bottom reading resumes follow within 60 DIPs");
        Check(WindowInteraction.LockGlyph(true)=="\uE72E"&&WindowInteraction.LockGlyph(false)=="\uE785","Closed padlock indicates locked and open padlock indicates unlocked");
        Check(WindowInteraction.LockAction(true).StartsWith("Unlock")&&WindowInteraction.LockHint(true).Contains("locked"),"Lock actions and hints agree with actual state");
        foreach(string phase in new[]{"checking","downloading","verifying","loading","switching","choose","error","ready"}){
            using var json=JsonDocument.Parse(JsonSerializer.Serialize(new{phase,required=phase!="ready",message="detail",activeModel=phase=="ready"?new{name="Turbo"}:null,selectedModel=new{name="candidate"}}));
            var view=SpeechSetupView.Read(json.RootElement);Check(view.Busy==(phase is "checking" or "downloading" or "verifying" or "loading" or "switching"),"Setup busy-state gating: "+phase);Check(view.Ready==(phase=="ready")&&view.SelectedModel=="candidate","Only validated ready model is active: "+phase);
        }
        state.Apply(Event("source-status",device));state.Apply(JsonSerializer.Serialize(new{type="audio-level",source=new{id=device.Id,generation=device.Generation,name=device.Name,identity=device.Identity},rms=20,dbfs=-64,low=true}));
        Check(state.AudioLevels["device"].Low&&state.AudioLevels["device"].Dbfs==-64,"Audio level metadata reaches presentation independently of captions");state.Apply(Event("reset",device with{Generation=5}));Check(state.AudioLevels.Count==0,"Reset clears obsolete signal meters");
    }
    static void LifecycleTests(){
        string Life(string type,Source source,long id,long revision,bool final=false,string text="words",bool terminal=true)=>JsonSerializer.Serialize(new{type,source=new{id=source.Id,generation=source.Generation,name=source.Name,identity=source.Identity},id,revision,final,text,terminal,capturedAt=id});
        var s=new CaptionState();s.Apply(Event("reset",A));s.Apply(Event("reset",B));
        s.Apply(Life("caption",A,1,1,text:"old"));s.Apply(Life("caption",A,2,2,text:"new"));s.Apply(Life("segment-retired",A,1,1));
        Check(s.Partials["a"].Text=="new","Retiring an older segment cannot clear a newer partial");
        s.Apply(Life("segment-retired",A,2,1,terminal:false));Check(s.Partials["a"].Text=="new","An old retirement revision cannot clear a newer hypothesis");
        s.Apply(Life("segment-retired",A,2,3,terminal:false));s.Apply(Life("caption",A,2,2));s.Apply(Life("caption",A,1,100));Check(s.Partials.Count==0,"Cleared revisions and older segments cannot revive after partial removal");
        s.Apply(Life("caption",A,2,4,text:"fresh"));Check(s.Partials["a"].Text=="fresh","A fresh revision may follow a nonterminal retirement");
        s.Apply(Life("caption",A,2,5,text:""));Check(s.Partials.Count==0,"An empty partial clears only obsolete text");
        s.Apply(Life("caption",A,2,6,true,"final"));Check(s.History.Count==1,"A later final survives an empty partial response");
        s.Apply(Life("caption",A,4,8,true,"four"));s.Apply(Life("caption",A,3,7,true,"three"));Check(s.History.Select(c=>c.Text).SequenceEqual(new[]{"final","three","four"}),"Out-of-order finals preserve capture ordering and history");
        s.Apply(Life("segment-retired",A,4,10));Check(s.History.Count==3,"Retirement never removes finalized history");
        s.Apply(JsonSerializer.Serialize(new{type="source-status",source=new{id=A.Id,generation=A.Generation,name=A.Name,identity=A.Identity},state="unavailable",message="Local transcription failed."}));s.Apply(Event("reset",A));
        Check(s.Sources["a"].State=="unavailable"&&s.Sources["a"].Message.Contains("failed"),"Caption resets preserve source failure information");
        s.Apply(JsonSerializer.Serialize(new{type="reset",source=new{id=A.Id,generation=A.Generation,name=A.Name,identity=A.Identity},state="waiting",message="Do not overwrite health"}));Check(s.Sources["a"].State=="unavailable","Even a reset carrying status-looking fields is not a health transition");
        Check(new SourceStatus(A,"stopped","Paused. Last failure: capture disconnected.").HasFailure,"Pausing retains a visible failure indication");
        var resumed=A with{Generation=3};s.Apply(Event("reset",resumed));s.Apply(Life("caption",A,10,100));Check(s.Partials.Count==0&&s.History.Count==3,"New generations reject late results while retaining same-identity finalized history");
        Check(s.Sources["a"].State=="unavailable","Reset during recovery does not report healthy waiting");
        var advanced=resumed with{Generation=4};s.Apply(Life("segment-retired",advanced,1,2));s.Apply(Event("reset",advanced));s.Apply(Life("caption",advanced,1,3));Check(s.Partials.Count==0,"Retirement arriving before a generation reset is retained");
        s.Apply(Event("source-removed",advanced));s.Apply(Event("source-status",advanced));s.Apply(Life("caption",advanced,2,4));Check(!s.Sources.ContainsKey("a")&&!s.Partials.ContainsKey("a"),"Removed generations cannot be recreated by statuses or results");
        var status=JsonSerializer.Deserialize<JsonElement>(JsonSerializer.Serialize(new{type="source-status",source=new{id=A.Id,generation=A.Generation,name=A.Name,identity=A.Identity},state="unavailable",message="Local transcription failed."}));
        var partial=JsonSerializer.Deserialize<JsonElement>(Life("caption",A,2,3));
        var metadata=new{generation=1,floor=0,finalized=1,latest=2,entries=new[]{new{id=1,revision=1,closed=true,cleared=true},new{id=2,revision=4,closed=false,cleared=true}}};
        string snapshot=JsonSerializer.Serialize(new{type="snapshot",sources=new{a=status},history=new[]{JsonSerializer.Deserialize<JsonElement>(Life("caption",A,1,1,true,"kept"))},partials=new{a=partial},segments=new{a=metadata}});
        s.Apply(snapshot);Check(s.Partials.Count==0&&s.History.Single().Text=="kept"&&s.Sources["a"].State=="unavailable","Reconnect snapshot carries retirement guards, history and persistent failure");
        s.Apply(Life("caption",A,2,5,text:"current"));Check(s.Partials["a"].Text=="current","Newer data is accepted after snapshot retirement");
        foreach(var state in new[]{"waiting","capturing","transcribing","delayed","unavailable","recovering","stopped"})Check(new SourceStatus(A,state,"detail").Label.Length>0,"Source health has a readable label: "+state);
    }
    static async Task SessionTest() {
        // Test harness uses the same private stdin handoff and the production Node server.
        using var handoff = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
        await using var client = new SessionClient(handoff.RootElement.GetProperty("url").GetString()!);
        var s = new CaptionState(); int snapshots=0; bool reconnect=false;long[] longNameIds=[];
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = client.RunAsync(async json => {
            s.Apply(json); using var doc = JsonDocument.Parse(json); string? type=doc.RootElement.GetProperty("type").GetString();
            if (type == "snapshot") {
                snapshots++;
                if (snapshots == 1) { Check(s.Sources.Count == 2 && s.ProcessSupported,"Authenticated initial snapshot"); Check(await client.SendAsync(new { type="refresh" }),"Refresh command sent"); }
                else if(snapshots==2) { Check(s.History.Count == 2 && s.History.Select(x=>x.Key).Distinct().Count()==2,"Reconnect snapshot does not duplicate finals or restore replaced sources"); await client.SendAsync(new {type="pause",paused=false}); }
                else if(snapshots==3){Check(s.History.Count==400&&s.History.Count(c=>c.Source.Id=="a")==200&&s.History.Count(c=>c.Source.Id=="b")==200,"Authenticated large reconnect snapshot obeys independent retention budgets");Check(s.History.Single(c=>c.Source.Id=="a"&&c.Id==237) is{Unfinished:true,Final:false}&&s.Partials.Count==0,"Large native snapshot preserves unfinished classification and retirement guards");await client.SendAsync(new{type="configure",slot="a",id="11:101",enabled=true});}
                else {Check(snapshots==4&&s.History.Where(c=>c.Source.Id=="a").Select(c=>c.Id).SequenceEqual(longNameIds),"Actual long-Unicode-name reconnect preserves live backend/native retention exactly");Check(s.Sources["a"].Source.Name==new string('界',199)&&s.Sources["a"].Source.Identity=="11:101","Wire source-name normalization preserves identity across reconnect");done.TrySetResult();}
            }
            if (type == "status" && doc.RootElement.GetProperty("message").GetString() == "stage-captions") {
                Check(s.History.Count == 2 && s.Partials.Count == 0 && s.History[0].Source.Id == "a","Interleaved live partials and finals over real WebSocket");
                await client.SendAsync(new {type="pause",paused=true});
            }
            if(type=="status"&&doc.RootElement.GetProperty("message").GetString()=="stage-retired-partial")Check(s.Partials.Count==1&&s.Partials.ContainsKey("b"),"Segment retirement is source-specific over authenticated WebSocket");
            if(type=="status"&&doc.RootElement.GetProperty("message").GetString()=="stage-newer-partial")Check(s.Partials["a"].Id==3&&s.Partials.ContainsKey("b"),"Older retirement cannot remove newer live text over WebSocket");
            if(type=="status"&&doc.RootElement.GetProperty("message").GetString()=="stage-preserved-error")Check(s.Sources["a"].State=="unavailable"&&s.Sources["a"].Message.Contains("failed")&&s.History.Count==2,"Production reset preserves inference failure and finalized history");
            if (type == "paused") {
                if (s.Paused) { Check(s.Paused,"Pause acknowledgement"); await client.SendAsync(new {type="clear"}); }
                else { Check(!s.Paused,"Resume acknowledgement"); await client.SendAsync(new {type="configure",slot="a",id="11:101",enabled=true}); }
            }
            if (type == "cleared") { Check(s.History.Count==0 && s.Partials.Count==0,"Clear broadcast through production server"); await client.SendAsync(new {type="refresh"}); }
            if (type == "status" && doc.RootElement.GetProperty("message").GetString() == "stage-reconnect" && !reconnect) { reconnect=true; client.Reconnect(); }
            if(type=="status"&&doc.RootElement.GetProperty("message").GetString()=="stage-retention"){Check(s.History.Count==400&&s.History.Single(c=>c.Source.Id=="a"&&c.Id==237).Unfinished,"Live backend/native history budgets and retained drafts agree before reconnect");client.Reconnect();}
            if (type == "status" && doc.RootElement.GetProperty("message").GetString() == "stage-complete") { Check(snapshots==3,"Exactly two requested reconnects before long-name coverage");Check(s.History.Count==400&&s.History.Single(c=>c.Source.Id=="a"&&c.Id==237) is{Final:true,Unfinished:false},"Fresh final replaces a retained snapshot draft without duplicating native history");await client.SendAsync(new{type="refresh"}); }
            if(type=="status"&&doc.RootElement.GetProperty("message").GetString()=="stage-long-name"){longNameIds=doc.RootElement.GetProperty("retainedIds").EnumerateArray().Select(v=>v.GetInt64()).ToArray();Check(longNameIds.Length<200&&s.History.Where(c=>c.Source.Id=="a").Select(c=>c.Id).SequenceEqual(longNameIds),"Live native and backend byte-budget decisions agree for a long Unicode source name");client.Reconnect();}
        }, _ => Task.CompletedTask);
        var winner = await Task.WhenAny(done.Task,run,Task.Delay(30000));
        if (winner == run) await run;
        Check(winner == done.Task,"Native WebSocket integration completed");
        await client.DisposeAsync(); await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
