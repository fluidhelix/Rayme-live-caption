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
        for (int i=2;i<=50;i++) s.Apply(Event("caption",replacement,i,true,capturedAt:i)); Check(s.History.Count == 30 && s.History[0].Id == 21,"History bounded to backend's 30 rows");
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
        foreach (string url in new[] { "https://127.0.0.1/?token="+new string('a',48), "http://localhost/?token="+new string('a',48), "http://example.com/?token="+new string('a',48), "http://127.0.0.1/?token=secret", "http://127.0.0.1/path?token="+new string('a',48) }) {
            bool rejected = false; try { _ = new SessionClient(url); } catch (ArgumentException) { rejected = true; } Check(rejected,"Only exact authenticated loopback sessions accepted");
        }
    }
    static async Task SessionTest() {
        // Test harness uses the same private stdin handoff and the production Node server.
        using var handoff = JsonDocument.Parse((await Console.In.ReadLineAsync())!);
        await using var client = new SessionClient(handoff.RootElement.GetProperty("url").GetString()!);
        var s = new CaptionState(); int snapshots=0; bool reconnect=false;
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var run = client.RunAsync(async json => {
            s.Apply(json); using var doc = JsonDocument.Parse(json); string? type=doc.RootElement.GetProperty("type").GetString();
            if (type == "snapshot") {
                snapshots++;
                if (snapshots == 1) { Check(s.Sources.Count == 2 && s.ProcessSupported,"Authenticated initial snapshot"); Check(await client.SendAsync(new { type="refresh" }),"Refresh command sent"); }
                else { Check(s.History.Count == 2 && s.History.Select(x=>x.Key).Distinct().Count()==2,"Reconnect snapshot does not duplicate finals or restore replaced sources"); await client.SendAsync(new {type="pause",paused=false}); }
            }
            if (type == "status" && doc.RootElement.GetProperty("message").GetString() == "stage-captions") {
                Check(s.History.Count == 2 && s.Partials.Count == 0 && s.History[0].Source.Id == "a","Interleaved live partials and finals over real WebSocket");
                await client.SendAsync(new {type="pause",paused=true});
            }
            if (type == "paused") {
                if (s.Paused) { Check(s.Paused,"Pause acknowledgement"); await client.SendAsync(new {type="clear"}); }
                else { Check(!s.Paused,"Resume acknowledgement"); await client.SendAsync(new {type="configure",slot="a",id="11:101",enabled=true}); }
            }
            if (type == "cleared") { Check(s.History.Count==0 && s.Partials.Count==0,"Clear broadcast through production server"); await client.SendAsync(new {type="refresh"}); }
            if (type == "status" && doc.RootElement.GetProperty("message").GetString() == "stage-reconnect" && !reconnect) { reconnect=true; client.Reconnect(); }
            if (type == "status" && doc.RootElement.GetProperty("message").GetString() == "stage-complete") { Check(snapshots==2,"Exactly one reconnect"); done.TrySetResult(); }
        }, _ => Task.CompletedTask);
        var winner = await Task.WhenAny(done.Task,run,Task.Delay(30000));
        if (winner == run) await run;
        Check(winner == done.Task,"Native WebSocket integration completed");
        await client.DisposeAsync(); await run.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
