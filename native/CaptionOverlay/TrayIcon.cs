using System;
using System.Drawing;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using Forms=System.Windows.Forms;

namespace Rayme.Overlay;

// Documented Shell API registration gives us the actual icon rectangle (including keyboard invocation).
// WinForms is retained only as a dependable, styled menu fallback if WPF flyout creation fails.
internal sealed class TrayIcon : IDisposable {
    const uint Callback=0x802A,Id=1;
    readonly Window owner;
    readonly HwndSource source;
    readonly IntPtr hwnd;
    readonly Icon icon;
    readonly Func<TrayAction[]> actions;
    readonly uint taskbarCreated;
    TrayFlyout? flyout;
    Forms.ContextMenuStrip? fallback;
    bool disposed;
    long lastSelection;
    public bool Available { get; private set; }
    [StructLayout(LayoutKind.Sequential)] struct Rect { public int Left,Top,Right,Bottom; }
    [StructLayout(LayoutKind.Sequential)] struct Point { public int X,Y; }
    [StructLayout(LayoutKind.Sequential)] struct IconIdentifier { public uint Size;public IntPtr Window;public uint Id;public Guid Guid; }
    [StructLayout(LayoutKind.Sequential,CharSet=CharSet.Unicode)] struct IconData {
        public uint Size;public IntPtr Window;public uint Id,Flags,Message;public IntPtr Icon;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=128)] public string Tip;
        public uint State,StateMask;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=256)] public string Info;
        public uint Version;
        [MarshalAs(UnmanagedType.ByValTStr,SizeConst=64)] public string InfoTitle;
        public uint InfoFlags;public Guid Guid;public IntPtr BalloonIcon;
    }
    [DllImport("shell32.dll",CharSet=CharSet.Unicode)] static extern bool Shell_NotifyIcon(uint message,ref IconData data);
    [DllImport("shell32.dll")] static extern int Shell_NotifyIconGetRect(ref IconIdentifier icon,out Rect rect);
    [DllImport("user32.dll")] static extern bool GetCursorPos(out Point point);
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern uint RegisterWindowMessage(string message);
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    [DllImport("user32.dll")] static extern uint GetDoubleClickTime();
    IconData Data()=>new(){Size=(uint)Marshal.SizeOf<IconData>(),Window=hwnd,Id=Id,Flags=1|2|4|0x80,Message=Callback,Icon=icon.Handle,Tip="Rayme · Live captions",Info="",InfoTitle="",Version=4};
    public TrayIcon(Window owner,Func<TrayAction[]> actions) {
        this.owner=owner;this.actions=actions;hwnd=new WindowInteropHelper(owner).Handle;source=HwndSource.FromHwnd(hwnd)!;
        using(var resource=Application.GetResourceStream(Theme.IconUri)!.Stream)using(var embedded=new Icon(resource))icon=(Icon)embedded.Clone();
        taskbarCreated=RegisterWindowMessage("TaskbarCreated");
        source.AddHook(Message);
        var data=Data();Available=Shell_NotifyIcon(0,ref data);
        if(!Available){source.RemoveHook(Message);icon.Dispose();throw new InvalidOperationException("Notification area unavailable");}
        Shell_NotifyIcon(4,ref data); // NOTIFYICON_VERSION_4: keyboard and accessibility activation.
    }
    IntPtr Message(IntPtr window,int message,IntPtr wParam,IntPtr lParam,ref bool handled) {
        if(disposed)return IntPtr.Zero;
        if((uint)message==taskbarCreated){var data=Data();Available=Shell_NotifyIcon(0,ref data);if(Available)Shell_NotifyIcon(4,ref data);}
        if((uint)message==Callback) {
            int action=(int)(lParam.ToInt64()&0xFFFF);
            if(action is 0x007B or 0x0400 or 0x0401) {
                long now=Environment.TickCount64;
                if(action==0x0400&&lastSelection!=0&&now-lastSelection<=GetDoubleClickTime()) { lastSelection=0;flyout?.Close();actions()[0].Execute(); }
                else { if(action==0x0400)lastSelection=now;owner.Dispatcher.BeginInvoke(ShowMenu); }
                handled=true;
            } // WM_CONTEXTMENU, NIN_SELECT, NIN_KEYSELECT
            if(action==0x0203){flyout?.Close();actions()[0].Execute();handled=true;} // double-click recovery
        }
        return IntPtr.Zero;
    }
    Bounds Anchor() {
        var id=new IconIdentifier { Size=(uint)Marshal.SizeOf<IconIdentifier>(),Window=hwnd,Id=Id };
        if(Shell_NotifyIconGetRect(ref id,out var r)==0)return new(r.Left,r.Top,Math.Max(1,r.Right-r.Left),Math.Max(1,r.Bottom-r.Top));
        GetCursorPos(out var cursor);return new(cursor.X,cursor.Y,1,1);
    }
    void ShowMenu() {
        if(disposed)return;
        try { flyout?.Close();flyout=new TrayFlyout(actions());flyout.Closed+=(_,_)=>ReturnTrayFocus();flyout.ShowAt(Anchor()); }
        catch { flyout?.Close();flyout=null;ShowFallback(); }
    }
    void ShowFallback() {
        fallback?.Dispose();fallback=FallbackMenu(actions());
        fallback.Closed+=(_,_)=>ReturnTrayFocus();
        var anchor=Anchor();SetForegroundWindow(hwnd);fallback.Show((int)anchor.X,(int)(anchor.Y+anchor.Height));
    }
    void ReturnTrayFocus(){if(!disposed&&Available){var data=Data();Shell_NotifyIcon(3,ref data);}}
    internal static Forms.ContextMenuStrip FallbackMenu(TrayAction[] actions) {
        var menu=new Forms.ContextMenuStrip { BackColor=Color.FromArgb(24,34,49),ForeColor=Color.FromArgb(244,247,252),Font=new Font("Segoe UI",10),ShowImageMargin=false,Renderer=new DarkMenuRenderer() };
        foreach(var item in actions)menu.Items.Add(item.Label,null,(_,_)=>item.Execute());
        return menu;
    }
    public void Dispose() {
        if(disposed)return;disposed=true;flyout?.Close();fallback?.Dispose();if(!source.IsDisposed)source.RemoveHook(Message);
        var data=Data();Shell_NotifyIcon(2,ref data);icon.Dispose();Available=false;
    }
    sealed class DarkMenuRenderer : Forms.ToolStripProfessionalRenderer {
        public DarkMenuRenderer():base(new MenuColors()){RoundedEdges=true;}
    }
    sealed class MenuColors : Forms.ProfessionalColorTable {
        public override Color MenuItemSelected=>Color.FromArgb(47,69,94);
        public override Color MenuItemBorder=>Color.FromArgb(145,217,255);
        public override Color ToolStripDropDownBackground=>Color.FromArgb(24,34,49);
        public override Color MenuBorder=>Color.FromArgb(96,113,139);
    }
}
