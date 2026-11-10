using System.Diagnostics;
using System.Runtime.InteropServices;
using NAudio.CoreAudioApi;

public record AppIdentity(int Pid, string Started, string Name)
{
    public string Id => $"{Pid}:{Started}";
}
public static class Discovery
{
    public static AppIdentity? Identity(int pid)
    {
        try { using var p = Process.GetProcessById(pid); return p.HasExited ? null : new(pid, p.StartTime.ToUniversalTime().Ticks.ToString(), p.ProcessName); }
        catch { return null; } // Inaccessible metadata cannot safely establish an identity.
    }
    public static bool Matches(int pid, string started) => Identity(pid)?.Started == started;
    public static AppIdentity Root(AppIdentity app, IReadOnlyDictionary<int, int> parents, Func<int, AppIdentity?> lookup)
    {
        var seen = new HashSet<int> { app.Pid };
        while (parents.TryGetValue(app.Pid, out int parent) && seen.Add(parent)) {
            var candidate = lookup(parent);
            if (candidate == null || candidate.Name != app.Name || long.Parse(candidate.Started) > long.Parse(app.Started)) break;
            app = candidate;
        }
        return app;
    }
    public static AppIdentity[] Candidates(IEnumerable<int> sessionPids, IReadOnlyDictionary<int, int> parents, Func<int, AppIdentity?> lookup) =>
        sessionPids.Where(pid => pid > 0).Select(lookup).OfType<AppIdentity>().Select(p => Root(p, parents, lookup)).DistinctBy(p => p.Id).OrderBy(p => p.Name).ToArray();
    public static AppIdentity[] Applications(MMDeviceEnumerator enumerator)
    {
        var pids = new HashSet<int>();
        foreach (var endpoint in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) {
            using (endpoint) {
                try {
                    var sessions = endpoint.AudioSessionManager.Sessions;
                    for (int i = 0; i < sessions.Count; i++) { using var session = sessions[i]; if (!session.IsSystemSoundsSession) pids.Add(checked((int)session.GetProcessID)); }
                } catch { /* A device can disappear during enumeration. */ }
            }
        }
        return Candidates(pids, ParentMap(), Identity);
    }
    // Documented process metadata only; no process memory or game state access.
    public static string[] AncestorIds(AppIdentity app, IReadOnlyDictionary<int,int> parents, Func<int,AppIdentity?> lookup)
    {
        var result = new List<string>(); var seen = new HashSet<int> { app.Pid };
        while (result.Count < 64 && parents.TryGetValue(app.Pid, out int pid) && seen.Add(pid)) {
            var parent = lookup(pid); if (parent == null || long.Parse(parent.Started) > long.Parse(app.Started)) break;
            result.Add(parent.Id); app = parent;
        }
        return result.ToArray();
    }
    public static Dictionary<int, int> ParentMap()
    {
        var result = new Dictionary<int, int>();
        IntPtr snapshot = CreateToolhelp32Snapshot(2, 0);
        if (snapshot == new IntPtr(-1)) return result;
        try {
            var entry = new Entry { Size = (uint)Marshal.SizeOf<Entry>(), Exe = "" };
            if (Process32FirstW(snapshot, ref entry)) do { result[(int)entry.Pid] = (int)entry.Parent; } while (Process32NextW(snapshot, ref entry));
        } finally { CloseHandle(snapshot); }
        return result;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    struct Entry { public uint Size, Usage, Pid; public UIntPtr Heap; public uint Module, Threads, Parent; public int Priority; public uint Flags; [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string Exe; }
    [DllImport("kernel32.dll")] static extern IntPtr CreateToolhelp32Snapshot(uint flags, uint pid);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32FirstW(IntPtr handle, ref Entry entry);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] [return: MarshalAs(UnmanagedType.Bool)] static extern bool Process32NextW(IntPtr handle, ref Entry entry);
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)] static extern bool CloseHandle(IntPtr handle);
}
