using System;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
namespace Rayme.Overlay;
internal sealed partial class OverlayWindow {
    string rootGroup="",rootProfile="";
    internal bool IsClickThrough=>clickThrough;
    internal bool HasTrayRecovery=>tray?.Available==true||smoke;
    string ArrangementKey(string key)=>key==rootGroup&&rootProfile!=""?rootProfile:key;
    internal CaptionPlacement SourcePlacement(string key)=>prefs.ForArrangement(ArrangementKey(key));
    internal void ScheduleDetachedSave()=>ScheduleSave();
    internal void ScheduleDisplayRecovery(){displayTimer.Stop();displayTimer.Start();}
    internal void RequestQuit()=>QuitRayme();
    internal void UpdateDetachedAppearance(DetachedCaptionWindow view)=>view.Appearance(prefs,SourcePlacement(view.Slot).Locked,clickThrough);
    internal void ToggleSourceLock(string key)=>SetSourceLock(key,!SourcePlacement(key).Locked);
    internal void SetSourceLock(string key,bool locked){
        var next=prefs.WithArrangement(ArrangementKey(key),SourcePlacement(key) with{Locked=locked});
        Change(key==rootGroup?next with{Locked=next.ForArrangement(ArrangementKey(key)).Locked}:next);
    }
    internal Bounds SeedDetached(string key){var combined=prefs.Placement??DesktopPlacement.Read(handle);int index=Array.IndexOf(ApplicationSlots.All,key);return new(combined.X+Math.Max(0,index)*24,combined.Y+Math.Max(0,index)*32,combined.Width,Math.Max(180,combined.Height/2));}
    internal void OpenSourceControls(Window owner){controlOwner=owner;OpenControls();}
    internal void HideGroup(){
        if(tray?.Available!=true&&!smoke)return;RememberWindows();ScheduleSave();groupHidden=true;
        if(controls!=null)controls.Owner=null;
        foreach(var view in detached.Values)view.Hide();Hide();
    }
    void RememberWindows(){
        if(rootGroup!=""&&IsVisible&&handle!=IntPtr.Zero&&WindowState==WindowState.Normal)
            prefs=prefs.WithArrangement(rootProfile,new(DesktopPlacement.Read(handle),DesktopPlacement.MonitorId(handle),prefs.Locked));
        foreach(var view in detached.Values)prefs=prefs.WithArrangement(view.Slot,view.Placement());
        prefs=prefs with{Pairings=layout.Pairings,PairOrders=layout.Orders};
    }
    void RestoreRootArrangement(){
        if(handle==IntPtr.Zero||rootGroup=="")return;var displays=DesktopPlacement.Displays();if(displays.Count==0)return;
        var placement=SourcePlacement(rootGroup);restoring=true;
        try{prefs=prefs with{Locked=placement.Locked};DesktopPlacement.Apply(handle,Preferences.Recover(placement.Placement??prefs.Placement,displays,placement.Monitor));}
        finally{restoring=false;}
    }
    internal PairingMode PairingFor(string key){
        var group=layout.Groups.FirstOrDefault(g=>g.Key==key);
        if(group==null||group.Slots.Contains("device"))return PairingMode.None;
        if(group.Slots.Length==2)return PairingMode.Detach;
        int count=layout.Targets(group.Slots[0]).Length;return count==0?PairingMode.None:count==1?PairingMode.Attach:PairingMode.Choose;
    }
    internal void PairingAction(string key,FrameworkElement anchor){
        var group=layout.Groups.FirstOrDefault(g=>g.Key==key);if(group==null)return;
        if(group.Slots.Length==2){SeparateGroup(key);return;}
        string source=group.Slots[0];var targets=layout.Targets(source);
        if(targets.Length==1){AttachSource(source,targets[0]);return;}
        if(targets.Length==0)return;
        var menu=new ContextMenu{PlacementTarget=anchor,Placement=System.Windows.Controls.Primitives.PlacementMode.Bottom};
        foreach(string target in targets){string name=state.Sources[target].Source.Name;var item=new MenuItem{Header=name+" · "+target.ToUpperInvariant(),ToolTip="Attach to "+name+" (Application "+target.ToUpperInvariant()+")"};Theme.Accessible(item,"Attach to "+name);item.Click+=(_,_)=>AttachSource(source,target);menu.Items.Add(item);}
        anchor.ContextMenu=menu;menu.IsOpen=true;
    }
    internal void AttachSource(string source,string target){
        var to=layout.GroupFor(target);if(to==null)return;RememberWindows();
        var oldTarget=SourcePlacement(to.Key);var oldSource=SourcePlacement(source);
        if(!layout.Attach(source,target))return;
        // Exchange the occupied pair position, or restore a standalone pair's
        // saved attached profile. This pure placement plan is locally tested.
        prefs=PairingPlacement.Attach(prefs,layout.GroupFor(source)!.Key,target,to.Slots.Length==2,oldSource,oldTarget);
        panelLayout="";Render();UpdateSettings();ScheduleSave();
    }
    void SeparateGroup(string key){RememberWindows();if(!layout.Separate(key))return;panelLayout="";Render();UpdateSettings();ScheduleSave();}
    void Detach(){layout.Synchronize(state);var group=layout.Groups.FirstOrDefault(g=>g.Slots.Length==2);if(group!=null)SeparateGroup(group.Key);}
    internal void Reattach(bool reveal=true){
        if(!state.Sources.ContainsKey("a")||!state.Sources.ContainsKey("b"))return;
        RememberWindows();layout.Reattach();panelLayout="";if(reveal)groupHidden=false;Render();UpdateSettings();ScheduleSave();
    }
    void RestoreAll(){
        Through(false);RememberWindows();
        foreach(var group in layout.Groups)prefs=prefs.WithArrangement(ArrangementKey(group.Key),SourcePlacement(group.Key) with{Locked=false});
        prefs=prefs with{Locked=false};Reveal();if(rootGroup!="")Recover(true);foreach(var view in detached.Values)view.Recover(true);
    }
    void ArrangePanels(string[] slots){
        layout.Synchronize(state);var groups=layout.Groups;
        string nextRoot=groups.FirstOrDefault(g=>g.Slots.Length==2)?.Key??
            (groups.Length==1||layout.Pairings.Any(p=>p.SequenceEqual(new[]{"a","b"}))?groups.FirstOrDefault()?.Key:"")??"";
        var root=groups.FirstOrDefault(g=>g.Key==nextRoot);string nextProfile=root==null?"":layout.Profile(root);
        string signature=string.Join("|",groups.Select(g=>g.Key))+":"+nextRoot+":"+nextProfile;
        if(signature==panelLayout)return;
        RememberWindows();foreach(var panel in panels.Values)panel.PrepareMove();
        if(controls!=null)controls.Owner=null;
        captions.Children.Clear();captions.RowDefinitions.Clear();foreach(var view in detached.Values)view.Body.Children.Clear();
        foreach(string key in detached.Keys.Where(k=>k==nextRoot||!groups.Any(g=>g.Key==k)).ToArray()){detached[key].CloseView();detached.Remove(key);}
        bool rootChanged=rootGroup!=nextRoot||rootProfile!=nextProfile;rootGroup=nextRoot;rootProfile=nextProfile;panelLayout=signature;
        foreach(var group in groups){
            Grid body;
            if(group.Key==rootGroup)body=captions;
            else{if(!detached.TryGetValue(group.Key,out var view))detached[group.Key]=view=new DetachedCaptionWindow(this,group.Key);body=view.Body;body.RowDefinitions.Clear();if(!groupHidden)view.Show();}
            for(int i=0;i<group.Slots.Length;i++){
                string slot=group.Slots[i];if(!panels.TryGetValue(slot,out var panel))panels[slot]=panel=new TranscriptPanel(slot,smoke);
                panel.BorderThickness=new(0,i>0?1:0,0,0);panel.BorderBrush=Theme.Brush("#292929");
                body.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});Grid.SetRow(panel,i);body.Children.Add(panel);
            }
        }
        if(groups.Length==0){captions.Children.Add(new TextBlock{Text=setupRequired?"Complete speech setup in Controls.":"Choose an audio source in Controls.",Margin=new(18),Foreground=Theme.Brush("#C0C0C0"),TextWrapping=TextWrapping.Wrap});if(!groupHidden)Show();}
        else if(rootGroup=="")Hide();
        else if(!groupHidden){Show();if(rootChanged)RestoreRootArrangement();}
        controlOwner=rootGroup!=""||detached.Count==0?this:detached.Values.First();if(controls!=null)controls.Owner=controlOwner;
        ApplyAppearance();ScheduleSave();
    }
}
