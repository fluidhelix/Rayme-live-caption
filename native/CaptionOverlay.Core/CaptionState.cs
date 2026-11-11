using System.Text.Json;

namespace Rayme.Overlay;

public record Source(string Id, long Generation, string Name, string Identity);
public record Caption(Source Source, long Id, bool Final, string Text, double CapturedAt) {
    public string Key => $"{Source.Id}:{Source.Generation}:{Id}:{Final}";
}
public record SourceStatus(Source Source, string State, string Message);
public record Choice(string Id, string Name) { public override string ToString() => Name; }

// Presentation state only. No audio, inference, process discovery or configuration authority.
public sealed class CaptionState {
    public List<Caption> History { get; } = [];
    public Dictionary<string, Caption> Partials { get; } = [];
    public Dictionary<string, SourceStatus> Sources { get; } = [];
    readonly Dictionary<string, long> generations = [], finalized = [];
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
        return id is "a" or "b" or "device" ? new(id, Num(s, "generation"), Str(s, "name", 200), Str(s, "identity", 512)) : null;
    }
    static Caption ReadCaption(JsonElement e) => new(ReadSource(e) ?? new("legacy", 0, "Playback output", "legacy"), Num(e, "id"), Flag(e, "final"), Str(e, "text"), e.TryGetProperty("capturedAt", out var time) && time.TryGetDouble(out double at) && double.IsFinite(at) ? at : 0);
    static List<Choice> Choices(JsonElement e, string name) => e.TryGetProperty(name, out var list) && list.ValueKind == JsonValueKind.Array
        ? list.EnumerateArray().Take(512).Select(x => new Choice(Str(x, "id", 512), Str(x, "name", 200) + (name == "apps" && Num(x, "pid") > 0 ? $" (PID {Num(x, "pid")})" : ""))).ToList() : [];
    bool Obsolete(Source s) => generations.GetValueOrDefault(s.Id) > s.Generation || (removed.Contains(s.Id) && generations.GetValueOrDefault(s.Id) >= s.Generation);
    void Track(Source s) {
        if (generations.GetValueOrDefault(s.Id) < s.Generation) {
            Partials.Remove(s.Id); finalized.Remove(s.Id);
            if (Sources.TryGetValue(s.Id, out var old) && old.Source.Identity != s.Identity) History.RemoveAll(x => x.Source.Id == s.Id);
        }
        generations[s.Id] = s.Generation; removed.Remove(s.Id);
    }
    void Trim() { var ordered = History.OrderBy(c => c.CapturedAt).TakeLast(30).ToArray(); History.Clear(); History.AddRange(ordered); }
    public void Apply(string json) { using var doc = JsonDocument.Parse(json); Apply(doc.RootElement); }
    void Apply(JsonElement e) {
        string type = Str(e, "type", 40);
        if (type == "snapshot") {
            History.Clear(); Partials.Clear(); Sources.Clear(); generations.Clear(); finalized.Clear(); removed.Clear();
            Apps = Choices(e, "apps"); Devices = Choices(e, "devices"); ProcessSupported = Flag(e, "processSupported"); Paused = Flag(e, "paused"); Status = Str(e, "status");
            if (e.TryGetProperty("sources", out var sources)) foreach (var item in sources.EnumerateObject()) Apply(item.Value);
            if (e.TryGetProperty("history", out var history)) foreach (var item in history.EnumerateArray()) {
                var c = ReadCaption(item);
                // Old finalized conversation may survive capture resets, but never a replaced application.
                if (c.Source.Id != "legacy" && (!Sources.TryGetValue(c.Source.Id, out var active) || active.Source.Identity != c.Source.Identity || c.Source.Generation > active.Source.Generation)) continue;
                if (!History.Any(x => x.Key == c.Key)) History.Add(c);
                if (!Obsolete(c.Source)) finalized[c.Source.Id] = Math.Max(finalized.GetValueOrDefault(c.Source.Id), c.Id);
            }
            Trim();
            if (e.TryGetProperty("partials", out var partials)) foreach (var p in partials.EnumerateObject()) Apply(p.Value);
            if (e.TryGetProperty("partial", out var partial) && partial.ValueKind == JsonValueKind.Object) Apply(partial);
            return;
        }
        if (type == "applications") { Apps = Choices(e, "apps"); ProcessSupported = Flag(e, "supported"); }
        if (type == "devices") Devices = Choices(e, "devices");
        if (type == "status") Status = Str(e, "message");
        if (type == "paused") Paused = Flag(e, "paused");
        if (type == "cleared") { History.Clear(); Partials.Clear(); }
        var source = ReadSource(e);
        if (source != null && Obsolete(source)) return;
        if (type is "source-status" or "reset" && source != null) {
            Track(source); Sources[source.Id] = new(source, type == "reset" ? "waiting" : Str(e, "state", 40), type == "reset" ? "Waiting for audio" : Str(e, "message"));
            if (type == "reset") Partials.Remove(source.Id);
        }
        if (type == "reset" && source == null) Partials.Remove("legacy");
        if (type == "source-removed" && source != null) {
            generations[source.Id] = source.Generation; removed.Add(source.Id); Sources.Remove(source.Id); Partials.Remove(source.Id); finalized.Remove(source.Id); History.RemoveAll(x => x.Source.Id == source.Id);
        }
        if (type != "caption") return;
        var caption = ReadCaption(e); var slot = caption.Source.Id;
        if (source != null) { Track(source); if (!Sources.ContainsKey(slot)) Sources[slot] = new(source, "capturing", ""); }
        if (caption.Id <= finalized.GetValueOrDefault(slot)) return;
        if (caption.Final) {
            finalized[slot] = caption.Id;
            if (!Partials.TryGetValue(slot, out var pending) || pending.Id <= caption.Id) Partials.Remove(slot);
            if (caption.Text.Length > 0) { History.Add(caption); Trim(); }
        } else if (!Partials.TryGetValue(slot, out var pending) || pending.Id <= caption.Id) Partials[slot] = caption;
    }
}
