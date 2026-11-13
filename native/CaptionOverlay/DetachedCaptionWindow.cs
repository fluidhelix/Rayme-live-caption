using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;
namespace Rayme.Overlay;
// A presentation-only peer: no SessionClient, audio worker, model or scheduler.
internal sealed class DetachedCaptionWindow:Window {
    readonly OverlayWindow host;
    internal readonly string Slot;
    internal readonly CaptionToolbar Toolbar;
    readonly Border surface;
    internal readonly Grid Body=new();
    IntPtr handle;
    bool ownedClose,restoring;
    internal DetachedCaptionWindow(OverlayWindow owner,string slot){
        host=owner;Slot=slot;Title="Rayme · "+slot.ToUpperInvariant();Icon=new BitmapImage(Theme.IconUri);Width=600;Height=250;MinWidth=300;MinHeight=180;
        WindowStyle=WindowStyle.None;ResizeMode=ResizeMode.CanResizeWithGrip;AllowsTransparency=true;Background=Brushes.Transparent;Foreground=Theme.Brush("#F2F2F2");FontFamily=new("Segoe UI Variable Text, Segoe UI");ShowActivated=false;
        WindowChrome.SetWindowChrome(this,new(){CaptionHeight=0,ResizeBorderThickness=new(7),CornerRadius=new(14),GlassFrameThickness=new(0),UseAeroCaptionButtons=false});
        Toolbar=new(host.HideGroup,()=>host.Reattach(),()=>host.ToggleSourceLock(slot),()=>host.OpenSourceControls(this));
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=new GridLength(36)});grid.RowDefinitions.Add(new());
        grid.Children.Add(new Border{BorderThickness=new(0,0,0,1),BorderBrush=Theme.Brush("#262626"),Child=Toolbar});Grid.SetRow(Body,1);grid.Children.Add(Body);
        surface=new(){CornerRadius=new(14),BorderThickness=new(1),BorderBrush=Theme.Brush("#515151"),Child=grid};Content=surface;
        WindowDrag.Install(this,()=>host.SourcePlacement(slot).Locked||host.IsClickThrough);
        SourceInitialized+=(_,_)=>{handle=new WindowInteropHelper(this).Handle;HwndSource.FromHwnd(handle)?.AddHook(Messages);Recover(false);host.UpdateDetachedAppearance(this);};
        LocationChanged+=(_,_)=>{if(!restoring)host.ScheduleDetachedSave();};SizeChanged+=(_,_)=>{if(!restoring)host.ScheduleDetachedSave();};
        Closing+=(_,e)=>{if(!ownedClose){e.Cancel=true;host.RequestQuit();}};
    }
    IntPtr Messages(IntPtr h,int m,IntPtr w,IntPtr l,ref bool handled){if(m is 0x007E or 0x02E0 or 0x001A)host.ScheduleDisplayRecovery();return IntPtr.Zero;}
    internal CaptionPlacement Placement()=>handle==IntPtr.Zero?host.SourcePlacement(Slot):new(DesktopPlacement.Read(handle),DesktopPlacement.MonitorId(handle),host.SourcePlacement(Slot).Locked);
    internal void Recover(bool primary){if(handle==IntPtr.Zero)return;var displays=DesktopPlacement.Displays();if(displays.Count==0)return;restoring=true;try{var saved=host.SourcePlacement(Slot);var rect=primary?null:IsLoaded?DesktopPlacement.Read(handle):saved.Placement??host.SeedDetached(Slot);DesktopPlacement.Apply(handle,Preferences.Recover(rect,displays,primary?displays[0].Id:saved.Monitor));}finally{restoring=false;}}
    internal void Appearance(Preferences prefs,bool locked,bool through){surface.Background=new SolidColorBrush(Color.FromArgb((byte)(prefs.BackgroundOpacity*255),0,0,0));Topmost=prefs.Topmost;ResizeMode=locked?ResizeMode.NoResize:ResizeMode.CanResizeWithGrip;WindowChrome.GetWindowChrome(this).ResizeBorderThickness=new(locked?0:7);if(handle!=IntPtr.Zero)DesktopPlacement.ClickThrough(handle,through);}
    internal void CloseView(){ownedClose=true;Close();}
}
