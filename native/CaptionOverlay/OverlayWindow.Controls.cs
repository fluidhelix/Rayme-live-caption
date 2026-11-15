using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace Rayme.Overlay;

internal sealed partial class OverlayWindow {
    readonly Dictionary<string,List<RadioButton>> colorControls = [];
    Slider? fontControl,opacityControl;
    readonly Dictionary<string,Button> customColorButtons=[];
    readonly Dictionary<string,TextBlock> colorTitles=[];
    readonly Dictionary<string,CheckBox> sourceLocks=[];
    Border? statusCard;
    StackPanel? applicationCards;
    Button? addApplication;
    readonly Dictionary<string,StackPanel> colorSections=[];
    readonly Dictionary<string,Choice> selectionDrafts=[];
    readonly HashSet<string> removingSlots=[];
    bool discoveryPending;
    long discoveryRevision;
    DateTime discoveryRequested;
    void RequestDiscovery(){
        if(discoveryPending&&(DateTime.UtcNow-discoveryRequested).TotalSeconds<5)return;
        discoveryPending=true;discoveryRequested=DateTime.UtcNow;_ = RefreshDiscovery();
    }
    async System.Threading.Tasks.Task RefreshDiscovery(){if(!await TrySend(new{type="refresh"}))discoveryPending=false;}
    void AddApplicationSlot(){
        string? slot=ApplicationSlots.All.FirstOrDefault(s=>!prefs.VisibleApplications.Contains(s)&&!removingSlots.Contains(s));if(slot==null)return;
        prefs=prefs with{VisibleApplications=prefs.VisibleApplications.Append(slot).Order().ToArray()};BuildAudioCards();UpdateSettings();ScheduleSave();
    }
    async void RemoveApplicationSlot(string slot){
        if(slot=="a"||removingSlots.Contains(slot))return;
        if(state.Sources.ContainsKey(slot)){
            removingSlots.Add(slot);
            if(!await TrySend(new{type="configure",slot,enabled=false}))removingSlots.Remove(slot);
            // Keep the configured card until the server acknowledges removal.
            UpdateSettings();return;
        }
        selectionDrafts.Remove(slot);prefs=prefs with{VisibleApplications=prefs.VisibleApplications.Where(s=>s!=slot).ToArray()};BuildAudioCards();UpdateSettings();ScheduleSave();
    }
    void BuildAudioCards(){
        if(applicationCards==null)return;
        foreach(var (slot,pair) in selectors)if(pair.Select.SelectedItem is Choice choice&&!removingSlots.Contains(slot))selectionDrafts[slot]=choice;
        applicationCards.Children.Clear();selectors.Clear();
        foreach(string slot in prefs.VisibleApplications.Append("device")){
            bool device=slot=="device";if(device&&addApplication!=null)applicationCards.Children.Add(addApplication);var card=new StackPanel();var heading=new DockPanel();
            var actions=new StackPanel{Orientation=Orientation.Horizontal};DockPanel.SetDock(actions,Dock.Right);heading.Children.Add(actions);
            var refresh=Theme.IconButton("\uE72C","Refresh application availability",(_,_)=>RequestDiscovery());actions.Children.Add(refresh);
            if(!device&&slot!="a"){var remove=Theme.IconButton("\uE74D","Remove Application "+slot.ToUpperInvariant(),(_,_)=>RemoveApplicationSlot(slot));actions.Children.Add(remove);}
            var label=new StackPanel();label.Children.Add(Theme.Text(device?"Whole-device fallback":"Application "+slot.ToUpperInvariant(),14,strong:true));
            label.Children.Add(Theme.Text(device?"All audio on the selected output":"Capture this application’s process tree",12,"#B9B9B9"));
            var enabled=new CheckBox{Content=label,Margin=new(0,0,0,10)};Theme.Accessible(enabled,device?"Enable whole-device fallback":"Enable Application "+slot.ToUpperInvariant());heading.Children.Add(enabled);card.Children.Add(heading);
            var select=new ComboBox{DisplayMemberPath="Name",SelectedValuePath="Id",Tag=device?"Choose a playback output":"Choose an application",ToolTip="Choose the current process identity; restarted apps require manual reselection"};
            Theme.Accessible(select,device?"Playback output":"Application "+slot.ToUpperInvariant()+" selection");select.DropDownOpened+=(_,_)=>RequestDiscovery();
            selectors[slot]=(select,enabled);card.Children.Add(select);applicationCards.Children.Add(Theme.Card(card));
            async void Configure(object? sender,RoutedEventArgs e){
                if(syncing)return;string? id=(select.SelectedItem as Choice)?.Id;
                if(enabled.IsChecked==true&&id==null){syncing=true;enabled.IsChecked=false;syncing=false;return;}
                await Send(new{type="configure",slot,id,enabled=enabled.IsChecked==true});
            }
            enabled.Checked+=Configure;enabled.Unchecked+=Configure;select.SelectionChanged+=(sender,e)=>{if(!syncing&&select.SelectedItem is Choice c)selectionDrafts[slot]=c;if(enabled.IsChecked==true)Configure(sender,e);};
        }
        if(addApplication!=null)addApplication.Visibility=prefs.VisibleApplications.Length>=5?Visibility.Collapsed:Visibility.Visible;
    }
    static readonly Choice[] Palette = [new("#80DFFF","Sky blue"),new("#FFD68A","Warm amber"),new("#CBB9FF","Soft lilac"),new("#9CE9BA","Mint green"),new("#FFB8D2","Rose pink"),new("#F1F4FA","Moon white")];

    static void Section(Panel panel,string glyph,string title,string description) {
        var row=new StackPanel { Orientation=Orientation.Horizontal,Margin=new Thickness(0,22,0,5) };
        var icon=Theme.Glyph(glyph,17);icon.Foreground=Theme.Brush("#DDDDDD");icon.Margin=new Thickness(0,0,10,0);row.Children.Add(icon);row.Children.Add(Theme.Text(title,17,strong:true));panel.Children.Add(row);
        var note=Theme.Text(description,12,"#B9B9B9");note.Margin=new Thickness(0,0,0,12);panel.Children.Add(note);
    }
    void OpenControls() {
        Through(false);RequestDiscovery();
        if(controls!=null){controls.Owner=controlOwner??this;controls.Show();controls.Activate();return;}
        var panel=new StackPanel { Margin=new Thickness(24,0,24,24) };
        controls=new ModernWindow("Controls","Make room for every conversation",panel) { Owner=controlOwner??this,Width=500,Height=800,MinWidth=400,MinHeight=400 };
        controls.Closed+=(_,_)=>{controls=null;selectors.Clear();colorControls.Clear();customColorButtons.Clear();colorTitles.Clear();sourceLocks.Clear();colorSections.Clear();applicationCards=null;addApplication=null;fontControl=opacityControl=null;processingChoices.Clear();processingLabels.Clear();};
        backendStatus=Theme.Text(state.Status,12,"#D5D5D5");Theme.Accessible(backendStatus,"Caption session status");
        statusCard=Theme.Card(backendStatus,new Thickness(14,12,14,12));statusCard.Margin=new Thickness(0,6,0,0);panel.Children.Add(statusCard);
        Section(panel,"\uE720","Audio sources","Choose up to five applications, or use one playback output. Add only the application slots you need. Application selection replaces fallback.");
        applicationCards=new StackPanel();panel.Children.Add(applicationCards);
        addApplication=Theme.Button("Add Application",(_,_)=>AddApplicationSlot(),"\uE710");addApplication.Name="AddApplication";addApplication.HorizontalAlignment=HorizontalAlignment.Left;
        BuildAudioCards();
        sourceStatus=Theme.Text("",12,"#B9B9B9");sourceStatus.Margin=new Thickness(2,0,2,0);panel.Children.Add(sourceStatus);
        Section(panel,"\uE8D2","Captions","A comfortable reading size and clear source accents, independent of the window’s size.");
        var sliders=new StackPanel();
        fontControl=Range(sliders,"Caption text size",16,60,prefs.FontSize,n=>{if(!syncing)Change(prefs with{FontSize=n});},false);
        sliders.Children.Add(new Border { Height=1,Background=Theme.Brush("#343434"),Margin=new Thickness(0,12,0,12) });
        opacityControl=Range(sliders,"Background opacity",.70,1,prefs.BackgroundOpacity,n=>{if(!syncing)Change(prefs with{BackgroundOpacity=n});},true);
        sliders.Children.Add(Theme.Text("Text remains fully opaque for dependable contrast.",12,"#B9B9B9"));panel.Children.Add(Theme.Card(sliders));
        var colors=new StackPanel();
        foreach(string slot in ApplicationSlots.All.Append("device")) {
            string label=slot=="device"?"Playback output":"Application "+slot.ToUpperInvariant();
            var colorGroup=new StackPanel();colorSections[slot]=colorGroup;colors.Children.Add(colorGroup);
            var rowTitle=Theme.Text(label+" accent",13,strong:true);colorTitles[slot]=rowTitle;rowTitle.Margin=new Thickness(0,slot=="a"?0:12,0,5);colorGroup.Children.Add(rowTitle);
            string selected=prefs.ColorFor(slot);
            var custom=Theme.Button("Custom color… "+selected,(_,_)=>OpenColorPicker(slot));customColorButtons[slot]=custom;colorGroup.Children.Add(custom);
            var choices=Palette.ToList();if(!choices.Any(c=>c.Id==selected))choices.Add(new(selected,"Custom saved accent"));
            var row=new WrapPanel();colorControls[slot]=[];
            foreach(var choice in choices) {
                var swatch=new RadioButton { GroupName="accent-"+slot,Background=Theme.Brush(choice.Id),Tag=choice.Id,IsChecked=choice.Id==selected,ToolTip=choice.Name,Style=(Style)FindResource("Swatch") };
                Theme.Accessible(swatch,label+" accent: "+choice.Name);
                swatch.Checked+=(_,_)=>{if(!syncing)Change(prefs.WithColor(slot,choice.Id));};
                row.Children.Add(swatch);colorControls[slot].Add(swatch);
            }
            colorGroup.Children.Add(row);
        }
        panel.Children.Add(Theme.Card(colors));
        Section(panel,"\uE950","Speech Recognition","Your model and processing preference are saved on this PC.");AddSpeechSettings(panel);
        Section(panel,"\uE737","Advanced","Position your captions once, then keep them where you need them.");
        var behavior=new StackPanel();
        topSetting=Setting(behavior,"Always on top","Keep captions above ordinary desktop windows.",prefs.Topmost,v=>Change(prefs with{Topmost=v}));
        Divider(behavior);
        lockSetting=Setting(behavior,"Lock position and size","Prevent accidental dragging and resizing.",prefs.Locked,v=>Change(prefs with{Locked=v}));
        foreach(string slot in ApplicationSlots.All)sourceLocks[slot]=Setting(behavior,"Lock "+slot.ToUpperInvariant()+" window","Independent detached-window position and size.",SourcePlacement(slot).Locked,v=>{if(!syncing)SetSourceLock(layout.GroupFor(slot)?.Key??slot,v);});
        Divider(behavior);
        throughSetting=Setting(behavior,"Click-through","Use the Rayme tray menu to interact again. Resets at restart.",false,Through);throughSetting.IsEnabled=tray?.Available==true;
        var restore=Theme.Button("Restore to primary monitor",(_,_)=>{RestoreAll();},"\uE9E9");restore.Margin=new Thickness(0,12,0,0);behavior.Children.Add(restore);panel.Children.Add(Theme.Card(behavior));
        Section(panel,"\uE768","Session controls","Capture and history controls are shared by every connected caption window.");
        var session=new WrapPanel();
        pauseButton=Theme.Button(state.Paused?"Resume capture":"Pause capture",async(_,_)=>await Send(new{type="pause",paused=!state.Paused}),style:"PrimaryButton");pauseButton.Name="PauseCapture";session.Children.Add(pauseButton);
        var clear=Theme.Button("Clear history",async(_,_)=>await Send(new{type="clear"}),"\uE74D");clear.Name="ClearHistory";clear.ToolTip="Remove caption history from all connected windows";session.Children.Add(clear);
        var refresh=Theme.Button("Refresh availability",async(_,_)=>await Send(new{type="refresh"}),"\uE72C");refresh.Name="RefreshAvailability";session.Children.Add(refresh);panel.Children.Add(Theme.Card(session));
        panel.Children.Add(Theme.Text("Saved automatically on this PC. Captions, audio and session credentials are never saved.",12,"#B9B9B9"));
        UpdateSettings();controls.Show();
    }
    void OpenColorPicker(string slot){
        string name=state.Sources.GetValueOrDefault(slot)?.Source.Name??(slot=="device"?"Windows playback":"Source "+slot.ToUpperInvariant());
        string current=prefs.ColorFor(slot);
        var picker=new ColorPicker(name,current,SourceColor.Default(slot)){Owner=controls??controlOwner??this};
        if(picker.ShowDialog()==true)Change(prefs.WithColor(slot,picker.Selected));
    }
    static void Divider(Panel panel)=>panel.Children.Add(new Border { Height=1,Background=Theme.Brush("#343434"),Margin=new Thickness(0,10,0,10) });
    CheckBox Setting(Panel panel,string name,string note,bool initial,Action<bool> action) {
        var content=new StackPanel();content.Children.Add(Theme.Text(name,14,strong:true));content.Children.Add(Theme.Text(note,12,"#B9B9B9"));
        var check=new CheckBox { Content=content,IsChecked=initial };Theme.Accessible(check,name);
        check.Checked+=(_,_)=>{if(!syncing)action(true);};check.Unchecked+=(_,_)=>{if(!syncing)action(false);};panel.Children.Add(check);return check;
    }
    static Slider Range(Panel panel,string name,double min,double max,double value,Action<double> change,bool percent) {
        var header=new DockPanel { Margin=new Thickness(0,0,0,8) };var number=Theme.Text(percent?value.ToString("P0"):value.ToString("0"),14,"#DDDDDD",true);number.MinWidth=44;number.TextAlignment=TextAlignment.Right;DockPanel.SetDock(number,Dock.Right);header.Children.Add(number);header.Children.Add(Theme.Text(name,13,strong:true));panel.Children.Add(header);
        var slider=new Slider { Minimum=min,Maximum=max,Value=value,SmallChange=percent?.01:1,LargeChange=percent?.05:4,IsSnapToTickEnabled=true,TickFrequency=percent?.01:1,IsMoveToPointEnabled=true };Theme.Accessible(slider,name);
        slider.ValueChanged+=(_,e)=>{number.Text=percent?e.NewValue.ToString("P0"):e.NewValue.ToString("0");change(e.NewValue);};panel.Children.Add(slider);return slider;
    }
}
