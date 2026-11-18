using System.Text.Json;
using Rayme.Overlay;
internal static class AccessibilityTests {
    static Source Source(string slot)=>new(slot,1,"App "+slot,slot+":100");
    static string Event(string type,string slot,long id=1,bool final=false,string text="words",long generation=1)=>JsonSerializer.Serialize(new{type,source=new{id=slot,generation,name="App "+slot,identity=slot+":"+(99+generation)},id,revision=id,final,text,capturedAt=id});
    static void DormantPairingRegression(Action<bool,string> check){
        var state=new CaptionState();
        var layout=new CaptionLayout();
        foreach(string slot in new[]{"a","c","d"}){state.Apply(Event("reset",slot));layout.Synchronize(state);}
        check(layout.Attach("c","d"),"C and D can pair while A/B is dormant");
        check(layout.Attach("a","c"),"A can exchange into C's upper position");
        var transitionPrefs=new Preferences{Pairings=layout.Pairings,PairOrders=layout.Orders};
        state.Apply(Event("reset","b"));layout.Synchronize(state);
        var groups=layout.Groups.Select(g=>g.Key).ToArray();
        check(groups.SequenceEqual(new[]{"a+d","b","c"}),"Enabling B after the exchange keeps each slot in exactly one group. Actual: "+string.Join(" | ",groups));
        check(layout.Pairings.Select(ApplicationSlots.Key).SequenceEqual(new[]{"a+d"})&&layout.Orders.ContainsKey("a+b")&&layout.Orders.ContainsKey("c+d"),"Exchange removes the dormant membership but keeps prior pair ordering for later use");
        var saved=Preferences.Parse(JsonSerializer.Serialize(transitionPrefs));
        var restored=new CaptionLayout(saved.Pairings,saved.PairOrders);
        restored.Synchronize(state);
        check(restored.Groups.Select(g=>g.Key).SequenceEqual(groups),"Saving before B returns and restarting with B active preserves the exchange");
        foreach(string slot in new[]{"c","d","b"}){state.Apply(Event("source-removed",slot));restored.Synchronize(state);}
        check(restored.Profile(restored.Groups.Single())=="a+d","Removing sources preserves A's new dormant A/D placement profile");
        state.Apply(Event("reset","b",generation:2));state.Apply(Event("reset","d",generation:2));restored.Synchronize(state);
        check(restored.Groups.Select(g=>g.Key).SequenceEqual(new[]{"a+d","b"}),"Re-enabling both former and current partners restores only the current pair");
    }
    static void FiveSlotPairingTransitions(Action<bool,string> check){
        var state=new CaptionState();
        var layout=new CaptionLayout([["b","a"],["d","c"]]);
        void Apply(string type,string slot,long generation=1){state.Apply(Event(type,slot,generation:generation));layout.Synchronize(state);}
        void Expect(params string[] keys){
            var members=layout.Groups.SelectMany(g=>g.Slots).Order();
            var savedMembers=layout.Pairings.SelectMany(p=>p).ToArray();
            check(layout.Groups.Select(g=>g.Key).SequenceEqual(keys)&&members.SequenceEqual(state.Sources.Keys.Order())
                &&savedMembers.Distinct().Count()==savedMembers.Length,"Active groups and saved pairs are disjoint: "+string.Join(" | ",keys));
        }
        foreach(string slot in new[]{"a","c","e"})Apply("reset",slot);
        Expect("a","c","e");
        check(layout.Attach("c","a"),"Attaching two singletons releases both former dormant pairs");
        Apply("reset","b");Apply("reset","d");
        Expect("a+c","b","d","e");
        check(layout.Attach("e","d")&&layout.Attach("b","c"),"Five slots support independent pairs and an exchange into the lower row");
        Expect("a+b","d+e","c");
        Apply("source-removed","a");
        Expect("d+e","b","c");
        check(layout.Attach("b","d"),"A singleton with a dormant partner can exchange into another pair");
        Apply("reset","a",2);
        Expect("b+e","a","c","d");
        check(layout.Separate("b+e")&&layout.Attach("c","d"),"Detaching and reattaching after the exchange reuses the former D-first order");
        Expect("d+c","a","b","e");
        Apply("source-removed","d");
        Expect("a","b","c","e");
        var saved=new Preferences{Pairings=layout.Pairings,PairOrders=layout.Orders};
        var loaded=Preferences.Parse(JsonSerializer.Serialize(saved));
        layout=new CaptionLayout(loaded.Pairings,loaded.PairOrders);
        layout.Synchronize(state);
        Apply("reset","d",2);
        Expect("d+c","a","b","e");

        layout=new CaptionLayout([["b","a"],["d","c"]]);
        layout.Synchronize(state);
        foreach(string slot in new[]{"b","c","d","e"})Apply("source-removed",slot,state.Sources[slot].Source.Generation);
        Expect("a");
        check(layout.Profile(layout.Groups.Single())=="b+a","Leaving only A active preserves its intentional B-first placement profile");
        Apply("reset","b",2);
        Expect("b+a");
        Apply("source-removed","b",2);Apply("source-removed","a",2);
        Apply("reset","a",3);Apply("reset","b",3);
        Expect("b+a");
    }
    public static void Run(Action<bool,string> check){
        DormantPairingRegression(check);
        FiveSlotPairingTransitions(check);
        check(ParagraphContinuation.Indent(2,0,112,400)==110,"Native continuation uses actual glyph origin, not estimated line counts");
        check(ParagraphContinuation.Indent(112,110,242,400)==240,"Repeated pruning replaces rather than accumulates continuation indentation");
        check(ParagraphContinuation.Indent(0,0,1000,300)==300&&ParagraphContinuation.Indent(double.NaN,0,30,300)==0,"Continuation is finite, bounded to one line and safe before layout");
        var state=new CaptionState();var layout=new CaptionLayout();
        foreach(string slot in ApplicationSlots.All){state.Apply(Event("reset",slot));layout.Synchronize(state);}
        check(layout.Groups.Select(g=>g.Key).SequenceEqual(new[]{"a+b","c","d","e"}),"Five sources initially retain combined A/B plus independent extra windows");
        check(layout.Groups.All(g=>g.Slots.Length<=2),"No group can contain more than two transcripts");
        check(layout.Targets("c").SequenceEqual(new[]{"a","b","d","e"}),"Standalone pairing targets include occupied upper and lower slots");
        var one=new CaptionState();one.Apply(Event("reset","a"));var ordinary=new CaptionLayout();ordinary.Synchronize(one);
        check(ordinary.Profile(ordinary.Groups.Single())=="a+b","One-source attached host keeps the familiar combined placement without overwriting detached A");
        ordinary.Attach("a","b");one.Apply(Event("reset","b"));ordinary.Synchronize(one);one.Apply(Event("source-removed","b"));ordinary.Synchronize(one);
        check(ordinary.Profile(ordinary.Groups.Single())=="a+b","Disabling a paired source preserves attached placement independently of saved detached positions");
        var identities=state.Sources.ToDictionary(p=>p.Key,p=>p.Value.Source);
        foreach(string slot in ApplicationSlots.All)state.Apply(Event("caption",slot,1,true,"Saved "+slot));
        check(layout.Attach("c","b")&&layout.Groups.Select(g=>g.Key).SequenceEqual(new[]{"a+c","b","d","e"}),"Choosing lower B exchanges C into the exact lower position and displaces B");
        check(layout.Attach("b","a")&&layout.Groups.First().Key=="b+c","Choosing upper A exchanges B into the exact upper position");
        check(layout.Separate("b+c")&&layout.GroupFor("b")?.Key=="b"&&layout.GroupFor("c")?.Key=="c","A pair separates in one operation");
        check(layout.Attach("c","b")&&layout.GroupFor("c")?.Key=="b+c","Reattaching remembers a pair's prior vertical order");
        check(layout.Attach("e","d")&&layout.Groups.Any(g=>g.Key=="d+e")&&layout.Groups.Length==3,"Two independent pairs and a singleton can coexist");
        check(!layout.Attach("c","a")&&!layout.Attach("f","a"),"A paired source or invented slot cannot be attached as a singleton");
        check(state.History.Count==5&&identities.All(p=>state.Sources[p.Key].Source==p.Value),"Pairing is presentation only and cannot alter caption history or identities");
        foreach(string left in ApplicationSlots.All)foreach(string right in ApplicationSlots.All.Where(s=>s!=left)){
            var arrangement=new CaptionLayout([]);arrangement.Synchronize(state);check(arrangement.Attach(left,right),"Arbitrary standalone pair is accepted: "+left+right);
            check(arrangement.Groups.All(g=>g.Slots.Length<=2)&&arrangement.Groups.SelectMany(g=>g.Slots).Distinct().Count()==5,"Arbitrary pairing preserves unique window membership: "+left+right);
        }
        check(new Preferences().VisibleApplications.SequenceEqual(new[]{"a"}),"First-run preferences show only Application A");
        var top=new CaptionPlacement(new(-1200,-900,600,400),"upper",true);var leftA=new CaptionPlacement(new(-1800,200,400,240),"left");var leftB=new CaptionPlacement(new(-1800,500,420,260),"left",true);
        var prefs=new Preferences{Placement=top.Placement,Monitor=top.Monitor,Locked=true,DetachedA=leftA,DetachedB=leftB};
        var startup=prefs.InitializeWindowProfiles() with{Locked=false};
        check(startup.ForArrangement("a+b").Locked&&startup.ForArrangement("a+b").Placement==top.Placement,"Switching the startup root cannot overwrite a legacy attached lock or placement");
        check(startup.DeviceWindow.Locked&&startup.DeviceWindow.Placement==top.Placement,"Previously unsaved whole-device placement migrates from the legacy main window");
        prefs=prefs.WithArrangement("a+b",top).WithArrangement("c+e",new(new(500,100,720,320),"main"));
        var exchange=PairingPlacement.Attach(prefs,"a+c","b",true,leftA,top);
        check(exchange.ForArrangement("a+c")==top&&exchange.ForSource("b")==leftA&&exchange.ForArrangement("a+b")==top&&exchange.ForSource("a")==leftA,"Placement exchange moves the displaced source to the initiating window and preserves other arrangements");
        var attachedAgain=PairingPlacement.Attach(prefs,"a+b","b",false,leftA,leftB);
        check(attachedAgain.ForArrangement("a+b")==top,"Reattaching standalone sources restores saved attached bounds rather than either detached position");
        for(int n=0;n<20;n++){
            prefs=prefs.WithSource("a",leftA with{Placement=leftA.Placement! with{Y=200+n}});
            check(prefs.ForArrangement("a+b")==top&&prefs.ForSource("b")==leftB,"Detached changes never overwrite attached or other-source placement");
        }
        prefs=prefs with{Pairings=layout.Pairings,PairOrders=layout.Orders,VisibleApplications=ApplicationSlots.All};
        prefs=prefs.WithColor("c","#ABCDEF").WithColor("d","#BADA55").WithColor("e","#A3D9B1");
        var loaded=Preferences.Parse(JsonSerializer.Serialize(prefs));check(loaded==prefs,"Five-source layouts, pair orders, locks and placement round-trip with value equality");
        var restart=new CaptionLayout(loaded.Pairings,loaded.PairOrders);restart.Synchronize(state);check(restart.Groups.Select(g=>g.Key).SequenceEqual(layout.Groups.Select(g=>g.Key)),"Restart restores presentation pairing without process identities in preferences");
        check(loaded.ForArrangement("c+e").Placement?.Width==720&&loaded.DetachedA.Placement?.Y==219,"Arbitrary pair placement remains independent of standalone arrangements");
        check((new Preferences{VisibleApplications=["a","b","b","f"],AttachedLayouts=new(){{"invalid",top}}}).Validate().VisibleApplications.SequenceEqual(new[]{"a","b"}),"Visible slot validation is bounded, unique and rejects invalid names");
        check((new Preferences{Pairings=[["a","b"],["b","c"],["d","e"],["a","a"]]}).Validate().Pairings.Length==2,"Corrupt grouping cannot duplicate a source across windows");
        var temp=Path.Combine(Path.GetTempPath(),"rayme-layout-budget-"+Guid.NewGuid(),"settings.json");
        try{
            check(prefs.Save(temp),"Valid five-source preferences save within the existing settings byte limit");var original=File.ReadAllText(temp);
            var oversized=prefs with{AttachedLayouts=ApplicationSlots.All.SelectMany(a=>ApplicationSlots.All.Where(b=>b!=a).Select(b=>a+"+"+b)).ToDictionary(k=>k,k=>new CaptionPlacement(new(1,1,400,300),new string('界',128)))};
            check(!oversized.Save(temp)&&File.ReadAllText(temp)==original,"An oversized settings document cannot overwrite valid saved preferences");
        }finally{Directory.Delete(Path.GetDirectoryName(temp)!,true);}
        foreach(string routine in new[]{"waiting","capturing","transcribing"}){
            var status=new SourceStatus(Source("c"),routine,"Routine diagnostic "+routine);
            check(SourceAvailability.Read(status)==new Availability("Available",true,false,"Application capture available"),"Routine recognition does not flicker visible availability: "+routine);
        }
        check(SourceAvailability.Read(new(Source("c"),"unavailable","Process exited")).Label=="Unavailable","Closed process stays unavailable without automatic reconnection");
        check(SourceAvailability.Read(new(Source("c"),"capturing","Last failure: speech failed")).Error,"Persistent recognizer failure is never shown as healthy");
        check(SourceAvailability.Read(new(Source("c"),"delayed","Overloaded")).Error,"Real overload remains an honest warning");
        var before=state.Sources["c"].Source;state.Apply("""{"type":"applications","supported":true,"apps":[{"id":"c:200","pid":42,"name":"App c"}]}""");
        check(state.Sources["c"].Source==before&&state.Apps.Single().Id=="c:200"&&state.ApplicationsRevision==1,"Discovery refresh cannot replace or reconnect the selected old process identity");
        foreach(string slot in ApplicationSlots.All)for(int id=2;id<=230;id++)state.Apply(Event("caption",slot,id,true,"bounded words"));
        check(state.History.Count==1000&&ApplicationSlots.All.All(s=>state.History.Count(c=>c.Source.Id==s)==200),"Five-source native history retains independent authoritative budgets");
        var ids=state.History.Select(c=>c.Key).ToArray();state.Apply(JsonSerializer.Serialize(new{type="snapshot",sources=state.Sources.ToDictionary(p=>p.Key,p=>new{type="source-status",source=new{id=p.Key,generation=1,name=p.Value.Source.Name,identity=p.Value.Source.Identity},state="waiting"}),history=state.History.Select(c=>new{source=new{id=c.Source.Id,generation=1,name=c.Source.Name,identity=c.Source.Identity},id=c.Id,text=c.Text,final=c.Final,capturedAt=c.CapturedAt}).ToArray()}));
        check(state.History.Select(c=>c.Key).SequenceEqual(ids),"Five-source reconnect cannot cross-attribute or drop retained history");
    }
}
