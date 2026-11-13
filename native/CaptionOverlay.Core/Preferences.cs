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
    public Bounds? Placement { get; init; }
    public string? Monitor { get; init; }
    static double Limit(double n, double fallback, double low, double high) => double.IsFinite(n) ? Math.Clamp(n, low, high) : fallback;
    static string Color(string? color, string fallback) => color != null && Regex.IsMatch(color, "^#[0-9A-Fa-f]{6}$") ? color : fallback;
    public static Bounds? ValidBounds(Bounds? b)=>b is not null&&new[]{b.X,b.Y,b.Width,b.Height}.All(double.IsFinite)&&b.Width>0&&b.Height>0?b:null;
    public CaptionPlacement ForSource(string slot)=>slot=="a"?DetachedA:DetachedB;
    public Preferences WithSource(string slot,CaptionPlacement value)=>slot=="a"?this with{DetachedA=value.Validate()}:this with{DetachedB=value.Validate()};
    public Preferences Validate() => this with {
        Version = 1, FontSize = Limit(FontSize, 26, 16, 60), BackgroundOpacity = Limit(BackgroundOpacity, .9, .70, 1),
        ColorA = Color(ColorA, "#80DFFF"), ColorB = Color(ColorB, "#FFD68A"), ColorDevice = Color(ColorDevice, "#CBB9FF"),
        DetachedA=(DetachedA??new()).Validate(),DetachedB=(DetachedB??new()).Validate(),
        Placement = ValidBounds(Placement),
        Monitor = Monitor?.Length <= 128 ? Monitor : null
    };
    public static Preferences Parse(string json) {
        try { var p = JsonSerializer.Deserialize<Preferences>(json); return p?.Version == 1 ? p.Validate() : new(); }
        catch (JsonException) { return new(); }
    }
    public static Preferences Load(string file) {
        try { return new FileInfo(file).Length <= 16384 ? Parse(File.ReadAllText(file)) : new(); }
        catch (IOException) { return new(); } catch (UnauthorizedAccessException) { return new(); }
    }
    public bool Save(string file) {
        try { Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file + ".tmp", JsonSerializer.Serialize(Validate(), new JsonSerializerOptions { WriteIndented = true })); File.Move(file + ".tmp", file, true); return true; }
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
