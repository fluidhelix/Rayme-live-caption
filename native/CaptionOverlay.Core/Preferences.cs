using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rayme.Overlay;

// Placement is persisted in physical desktop pixels. WPF sizes remain independent DIPs.
public record Bounds(double X, double Y, double Width, double Height);
public record Display(string Id, Bounds Work, double Scale = 1);
public sealed record CaptionPlacement(Bounds? Placement=null,string? Monitor=null,bool Locked=false) {
    public CaptionPlacement Validate()=>this with{Placement=Preferences.ValidBounds(Placement),Monitor=Monitor?.Length<=128?Monitor:null};
}
public sealed record Preferences {
    public int Version { get; init; } = 1;
    public double FontSize { get; init; } = 26;
    public double BackgroundOpacity { get; init; } = .90;
    public bool Topmost { get; init; } = true;
    public bool Locked { get; init; }
    public string ColorA { get; init; } = "#80DFFF";
    public string ColorB { get; init; } = "#FFD68A";
    public string ColorDevice { get; init; } = "#CBB9FF";
    public CaptionPlacement DetachedA {get;init;}=new();
    public CaptionPlacement DetachedB {get;init;}=new();
    public CaptionPlacement DetachedC {get;init;}=new();
    public CaptionPlacement DetachedD {get;init;}=new();
    public CaptionPlacement DetachedE {get;init;}=new();
    public CaptionPlacement DeviceWindow {get;init;}=new();
    public string ColorC {get;init;}="#CBB9FF";
    public string ColorD {get;init;}="#9CE9BA";
    public string ColorE {get;init;}="#FFB8D2";
    public string[] VisibleApplications {get;init;}=["a"];
    public string[][] Pairings {get;init;}=[["a","b"]];
    public Dictionary<string,string[]> PairOrders {get;init;}=new(){{"a+b",["a","b"]}};
    public Dictionary<string,CaptionPlacement> AttachedLayouts {get;init;}=[];
    public Bounds? Placement { get; init; }
    public string? Monitor { get; init; }
    static double Limit(double n, double fallback, double low, double high) => double.IsFinite(n) ? Math.Clamp(n, low, high) : fallback;
    static string Color(string? color, string fallback) => color != null && Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$") ? color : fallback;
    public static Bounds? ValidBounds(Bounds? b)=>b is not null&&new[]{b.X,b.Y,b.Width,b.Height}.All(double.IsFinite)&&b.Width>0&&b.Height>0?b:null;
    public string ColorFor(string slot)=>slot switch{"a"=>ColorA,"b"=>ColorB,"c"=>ColorC,"d"=>ColorD,"e"=>ColorE,_=>ColorDevice};
    public Preferences WithColor(string slot,string color)=>slot switch{"a"=>this with{ColorA=color},"b"=>this with{ColorB=color},"c"=>this with{ColorC=color},"d"=>this with{ColorD=color},"e"=>this with{ColorE=color},"device"=>this with{ColorDevice=color},_=>throw new ArgumentException("Unknown source")};
    public CaptionPlacement ForSource(string slot)=>slot switch{"a"=>DetachedA,"b"=>DetachedB,"c"=>DetachedC,"d"=>DetachedD,"e"=>DetachedE,"device"=>DeviceWindow,_=>throw new ArgumentException("Unknown source")};
    public Preferences WithSource(string slot,CaptionPlacement value)=>slot switch{"a"=>this with{DetachedA=value.Validate()},"b"=>this with{DetachedB=value.Validate()},"c"=>this with{DetachedC=value.Validate()},"d"=>this with{DetachedD=value.Validate()},"e"=>this with{DetachedE=value.Validate()},"device"=>this with{DeviceWindow=value.Validate()},_=>throw new ArgumentException("Unknown source")};
    public CaptionPlacement ForArrangement(string key)=>ApplicationSlots.PairKey(key)?AttachedLayouts.GetValueOrDefault(key)??(key=="a+b"?new(Placement,Monitor,Locked):new()):ForSource(key);
    public Preferences WithArrangement(string key,CaptionPlacement value){
        if(!ApplicationSlots.PairKey(key))return WithSource(key,value);
        var layouts=new Dictionary<string,CaptionPlacement>(AttachedLayouts){[key]=value.Validate()};
        return key=="a+b"?this with{AttachedLayouts=layouts,Placement=value.Placement,Monitor=value.Monitor}:this with{AttachedLayouts=layouts};
    }
    public Preferences InitializeWindowProfiles(){
        // Materialize legacy attached lock/placement before a different active
        // root window updates the legacy Locked field during session startup.
        var next=WithArrangement("a+b",ForArrangement("a+b"));
        return DeviceWindow.Placement==null?next with{DeviceWindow=new(Placement,Monitor,Locked)}:next;
    }
    public Preferences Validate() => this with {
        Version = 1, FontSize = Limit(FontSize, 26, 16, 60), BackgroundOpacity = Limit(BackgroundOpacity, .9, .70, 1),
        ColorA = Color(ColorA, "#80DFFF"), ColorB = Color(ColorB, "#FFD68A"), ColorDevice = Color(ColorDevice, "#CBB9FF"),
        DetachedA=(DetachedA??new()).Validate(),DetachedB=(DetachedB??new()).Validate(),
        DetachedC=(DetachedC??new()).Validate(),DetachedD=(DetachedD??new()).Validate(),DetachedE=(DetachedE??new()).Validate(),DeviceWindow=(DeviceWindow??new()).Validate(),
        ColorC=Color(ColorC,SourceColor.Default("c")),ColorD=Color(ColorD,SourceColor.Default("d")),ColorE=Color(ColorE,SourceColor.Default("e")),
        VisibleApplications=ApplicationSlots.All.Where(s=>s=="a"||(VisibleApplications??[]).Contains(s)).ToArray(),
        Pairings=new CaptionLayout(Pairings??[],PairOrders).Pairings,
        PairOrders=new CaptionLayout(Pairings??[],PairOrders).Orders,
        AttachedLayouts=(AttachedLayouts??[]).Where(p=>ApplicationSlots.PairKey(p.Key)).Take(20).ToDictionary(p=>p.Key,p=>(p.Value??new()).Validate()),
        Placement = ValidBounds(Placement),
        Monitor = Monitor?.Length <= 128 ? Monitor : null
    };
    // Records compare collection fields by reference by default; preferences
    // retain value equality across validation and JSON round-trips.
    public bool Equals(Preferences? other)=>other!=null&&Version==other.Version&&FontSize==other.FontSize&&BackgroundOpacity==other.BackgroundOpacity&&Topmost==other.Topmost&&Locked==other.Locked&&Placement==other.Placement&&Monitor==other.Monitor&&
        ApplicationSlots.All.Append("device").All(s=>ForSource(s)==other.ForSource(s)&&ColorFor(s)==other.ColorFor(s))&&
        VisibleApplications.SequenceEqual(other.VisibleApplications)&&Pairings.Select(ApplicationSlots.Key).SequenceEqual(other.Pairings.Select(ApplicationSlots.Key))&&AttachedLayouts.OrderBy(p=>p.Key).SequenceEqual(other.AttachedLayouts.OrderBy(p=>p.Key))&&PairOrders.OrderBy(p=>p.Key).Select(p=>p.Key+":"+ApplicationSlots.Key(p.Value)).SequenceEqual(other.PairOrders.OrderBy(p=>p.Key).Select(p=>p.Key+":"+ApplicationSlots.Key(p.Value)));
    public override int GetHashCode()=>HashCode.Combine(Version,FontSize,BackgroundOpacity,Topmost,Locked,Placement,Monitor,ColorA);
    public static Preferences Parse(string json) {
        try { var p = JsonSerializer.Deserialize<Preferences>(json); return p?.Version == 1 ? p.Validate() : new(); }
        catch (JsonException) { return new(); }
    }
    public static Preferences Load(string file) {
        try { return new FileInfo(file).Length <= 16384 ? Parse(File.ReadAllText(file)) : new(); }
        catch (IOException) { return new(); } catch (UnauthorizedAccessException) { return new(); }
    }
    public bool Save(string file) {
        try { var json=JsonSerializer.Serialize(Validate(),new JsonSerializerOptions{WriteIndented=true});if(System.Text.Encoding.UTF8.GetByteCount(json)>16384)return false;Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file + ".tmp", json); File.Move(file + ".tmp", file, true); return true; }
        catch (IOException) { return false; } catch (UnauthorizedAccessException) { return false; }
    }
    public static Bounds Recover(Bounds? saved, IReadOnlyList<Display> displays, string? preferred = null) {
        if (displays.Count == 0) throw new ArgumentException("At least one display is required");
        if (saved != null && !new[] { saved.X, saved.Y, saved.Width, saved.Height }.All(double.IsFinite)) saved = null;
        var target = displays.FirstOrDefault(d => d.Id == preferred) ?? displays.OrderByDescending(d => Intersection(saved, d.Work)).First();
        var w = target.Work; double scale = target.Scale;
        double width = Math.Clamp(saved?.Width ?? 640 * scale, Math.Min(300 * scale, w.Width), w.Width);
        double height = Math.Clamp(saved?.Height ?? 360 * scale, Math.Min(180 * scale, w.Height), w.Height);
        double x = saved?.X ?? w.X + (w.Width - width) / 2, y = saved?.Y ?? w.Y + 40 * scale;
        return new(Math.Clamp(x, w.X, w.X + w.Width - width), Math.Clamp(y, w.Y, w.Y + w.Height - height), width, height);
    }
    static double Intersection(Bounds? a, Bounds b) => a == null ? 0 : Math.Max(0, Math.Min(a.X+a.Width,b.X+b.Width)-Math.Max(a.X,b.X))*Math.Max(0,Math.Min(a.Y+a.Height,b.Y+b.Height)-Math.Max(a.Y,b.Y));
}
