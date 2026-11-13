using System;
using System.Linq;
using System.Windows;
namespace Rayme.Overlay;
internal sealed partial class OverlayWindow {
    internal bool IsClickThrough=>clickThrough;
    internal CaptionPlacement SourcePlacement(string slot)=>prefs.ForSource(slot);
    internal void ScheduleDetachedSave()=>ScheduleSave();
    internal void ScheduleDisplayRecovery(){displayTimer.Stop();displayTimer.Start();}
    internal void RequestQuit()=>QuitRayme();
    internal void UpdateDetachedAppearance(DetachedCaptionWindow view)=>view.Appearance(prefs,SourcePlacement(view.Slot).Locked,clickThrough);
    internal void ToggleSourceLock(string slot)=>Change(prefs.WithSource(slot,SourcePlacement(slot) with{Locked=!SourcePlacement(slot).Locked}));
    internal Bounds SeedDetached(string slot){var combined=prefs.Placement??DesktopPlacement.Read(handle);return new(combined.X+(slot=="b"?32:0),combined.Y+(slot=="b"?48:0),combined.Width,Math.Max(180,combined.Height/2));}
    internal void OpenSourceControls(Window owner){controlOwner=owner;OpenControls();}
    internal void HideGroup(){
        if(tray?.Available!=true&&!smoke)return;groupHidden=true;
        if(controls!=null)controls.Owner=null;
        foreach(var view in detached.Values)view.Hide();Hide();
    }
    void Detach(){
        if(layout.Detached||!layout.Detach(state))return;
        if(!smoke)Save();if(controls!=null)controls.Owner=null;
        foreach(string slot in new[]{"a","b"})detached[slot]=new DetachedCaptionWindow(this,slot);
        foreach(var view in detached.Values)view.Show();
        Render();controlOwner=detached["a"];if(controls!=null)controls.Owner=controlOwner;
        Hide();UpdateSettings();
    }
    internal void Reattach(bool reveal=true){
        if(!layout.Detached)return;
        foreach(var panel in panels.Values)panel.PrepareMove();
        foreach(var view in detached.Values){prefs=prefs.WithSource(view.Slot,view.Placement());view.Body.Children.Clear();}
        layout.Reattach();panelLayout="";
        if(controls!=null)controls.Owner=null;
        foreach(var view in detached.Values)view.CloseView();detached.Clear();
        controlOwner=this;if(reveal&&!quitRequested){groupHidden=false;Show();Recover(false);}if(controls!=null)controls.Owner=this;
        Render();UpdateSettings();ScheduleSave();
    }
    void RestoreAll(){
        Through(false);Change(prefs with{Locked=false,DetachedA=prefs.DetachedA with{Locked=false},DetachedB=prefs.DetachedB with{Locked=false}});Reveal();
        if(layout.Detached)foreach(var view in detached.Values)view.Recover(true);else Recover(true);
    }
}
