using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Windows.Interop;

namespace Rayme.Overlay;

internal static class DesktopPlacement {
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left, Top, Right, Bottom; }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct MonitorInfo {
        public int Size; public Rect Monitor, Work; public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Device;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)] struct DisplayDevice {
        public int Size;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Description;
        public uint Flags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Id;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string Key;
    }
    delegate bool MonitorCallback(IntPtr monitor, IntPtr dc, ref Rect bounds, IntPtr data);
    [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr dc, IntPtr clip, MonitorCallback callback, IntPtr data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool GetMonitorInfo(IntPtr monitor, ref MonitorInfo info);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern bool EnumDisplayDevices(string device, uint index, ref DisplayDevice info, uint flags);
    [DllImport("shcore.dll")] static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint x, out uint y);
    [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr window, out Rect rect);
    [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr window, uint flags);
    [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr window, IntPtr after, int x, int y, int width, int height, uint flags);
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] static extern IntPtr GetWindowLongPtr(IntPtr window, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] static extern IntPtr SetWindowLongPtr(IntPtr window, int index, IntPtr style);
    static MonitorInfo Info(IntPtr monitor) { var info = new MonitorInfo { Size = Marshal.SizeOf<MonitorInfo>(), Device = "" }; if (!GetMonitorInfo(monitor, ref info)) throw new InvalidOperationException("Could not read monitor placement"); return info; }
    static string DisplayId(string device) {
        var info = new DisplayDevice { Size = Marshal.SizeOf<DisplayDevice>(), Name = "", Description = "", Id = "", Key = "" };
        // EDD_GET_DEVICE_INTERFACE_NAME avoids relying only on ordinal DISPLAY1/2 names.
        return EnumDisplayDevices(device,0,ref info,1) && info.Id.Length > 0 ? info.Id : device;
    }
    public static List<Display> Displays() {
        var displays = new List<(Display Display, bool Primary)>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr m, IntPtr dc, ref Rect r, IntPtr data) => {
            var i = Info(m); var w = i.Work;
            GetDpiForMonitor(m, 0, out var dpi, out _);
            displays.Add((new(DisplayId(i.Device), new(w.Left,w.Top,w.Right-w.Left,w.Bottom-w.Top), dpi >= 96 ? dpi / 96.0 : 1), (i.Flags & 1) != 0)); return true;
        }, IntPtr.Zero);
        return displays.OrderByDescending(x => x.Primary).Select(x => x.Display).ToList();
    }
    public static Bounds Read(IntPtr window) { if (!GetWindowRect(window, out var r)) throw new InvalidOperationException("Could not read window placement"); return new(r.Left,r.Top,r.Right-r.Left,r.Bottom-r.Top); }
    public static string MonitorId(IntPtr window) => DisplayId(Info(MonitorFromWindow(window, 2)).Device);
    public static void Apply(IntPtr window, Bounds bounds) => SetWindowPos(window, IntPtr.Zero, (int)Math.Round(bounds.X),(int)Math.Round(bounds.Y),(int)Math.Round(bounds.Width),(int)Math.Round(bounds.Height), 0x14); // NOZORDER | NOACTIVATE
    public static void ClickThrough(IntPtr window, bool enabled) {
        const int extendedStyle = -20; const long transparent = 0x20, noActivate = 0x08000000;
        long style = GetWindowLongPtr(window, extendedStyle).ToInt64();
        SetWindowLongPtr(window, extendedStyle, new IntPtr(enabled ? style | transparent | noActivate : style & ~(transparent | noActivate)));
    }
}
