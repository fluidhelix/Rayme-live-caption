using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shell;
using System.Windows.Threading;
using System.Windows.Media.Imaging;
using System.Text.Json;

namespace Rayme.Overlay;

internal sealed partial class OverlayWindow : Window {
    readonly SessionClient? client;
    readonly bool smoke;
    readonly CaptionState state = new();
    readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rayme", "CaptionOverlay", "settings.json");
    Preferences prefs;
    readonly Border surface = new() { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), BorderBrush = Brush("#515151"), Padding = new Thickness(0) };
    readonly Grid captions = new();
    readonly CaptionToolbar toolbar;
    readonly CaptionLayout layout=new();
    readonly Dictionary<string,DetachedCaptionWindow> detached=[];
    bool groupHidden,transportConnected;
    Window? controlOwner;
    readonly Dictionary<string, TranscriptPanel> panels = [];
    string panelLayout="";
    readonly Button lockButton;
    readonly Button settingsButton;
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer displayTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    TrayIcon? tray;
    Window? controls;
    readonly Dictionary<string, (ComboBox Select, CheckBox Enabled)> selectors = [];
    TextBlock? backendStatus, sourceStatus;
    Button? pauseButton;
    CheckBox? lockSetting, topSetting, throughSetting;
    bool syncing, restoring, closing, clickThrough;
    string connectionText = "Connecting…";
    IntPtr handle;
    internal string ExitCapability="";
    bool quitRequested, backendClosed,sessionStarted;
    internal void BackendClosed(){backendClosed=true;Close();}
    void QuitRayme(){
        if(quitRequested)return;quitRequested=true;
        if(backendClosed||smoke){Close();return;}
        Console.Out.WriteLine(JsonSerializer.Serialize(new{type="quit-rayme",capability=ExitCapability}));Console.Out.Flush();
        connectionText="Closing Rayme — stopping audio and speech recognition…";UpdateConnection();
    }
    public bool SmokePassed { get; private set; }
    static SolidColorBrush Brush(string color) => new((Color)ColorConverter.ConvertFromString(color));
    static Button Action(string text, RoutedEventHandler handler) => Theme.Button(text,handler);
    public OverlayWindow(SessionClient? session, bool isSmoke = false) {
        client = session; smoke = isSmoke;transportConnected=isSmoke; prefs = smoke ? new() : Preferences.Load(settingsFile);layout=new(prefs.Pairings,prefs.PairOrders);
        Icon = new BitmapImage(Theme.IconUri); Title = "Rayme Smart Live Captions"; Width = 640; Height = 360; MinWidth = 300; MinHeight = 180;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip; AllowsTransparency = true;
        Background = Brushes.Transparent; Foreground = Brushes.White; FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        ShowInTaskbar = true; ShowActivated = false; Topmost = prefs.Topmost;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(7), CornerRadius = new CornerRadius(14), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        toolbar=new CaptionToolbar(HideGroup,()=>PairingAction(rootGroup,toolbar!.Layout),()=>Change(prefs with{Locked=!prefs.Locked}),()=>OpenSourceControls(this));
        lockButton=toolbar.Lock;settingsButton=toolbar.Settings;
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=new GridLength(36)});grid.RowDefinitions.Add(new());
        grid.Children.Add(new Border{BorderBrush=Theme.Brush("#262626"),BorderThickness=new(0,0,0,1),Child=toolbar});
        Grid.SetRow(captions,1);grid.Children.Add(captions);surface.Child=grid;Content=surface;
        WindowDrag.Install(this,()=>prefs.Locked||clickThrough);
        saveTimer.Tick += (_,_) => { saveTimer.Stop(); Save(); };
        displayTimer.Tick += (_,_) => { displayTimer.Stop(); Recover(false);foreach(var view in detached.Values)view.Recover(false); };
        SourceInitialized += (_,_) => {
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
            Recover(false); ApplyAppearance();
        };
        LocationChanged += (_,_) => ScheduleSave(); SizeChanged += (_,_) => ScheduleSave();
        Loaded += async (_,_) => {
            if(sessionStarted)return;sessionStarted=true;
#if !RAYME_DISTRIBUTION
            if (smoke) { await SmokeAsync(); return; }
#endif
            CreateTray(); ApplyAppearance(); Render();
            Console.Out.WriteLine("{\"type\":\"overlay-ready\"}"); // Control acknowledgement only; no credentials or captions.
            if (client != null) await RunClient();
        };
        Closing += (_,e) => { if(!smoke&&!backendClosed){e.Cancel=true;if(quitRequested)return;if(tray?.Available==true)HideGroup();else QuitRayme();return;} closing = true; saveTimer.Stop(); displayTimer.Stop(); if (!smoke) Save(); };
        Closed += async (_,_) => {
            foreach(var view in detached.Values)view.CloseView();detached.Clear();controls?.Close(); tray?.Dispose(); if (client != null) await client.DisposeAsync();
        };
    }
    IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (message is 0x007E or 0x02E0 or 0x001A) { displayTimer.Stop(); displayTimer.Start(); } // display, DPI, work-area changes
        return IntPtr.Zero;
    }
    void ScheduleSave() { if (restoring || closing || handle == IntPtr.Zero || smoke) return; saveTimer.Stop(); saveTimer.Start(); }
    void Save() {
        RememberWindows();
        if (!prefs.Save(settingsFile)) { connectionText = "Settings could not be saved; check local folder access."; UpdateConnection(); }
    }
    void Recover(bool reset) {
        if (handle == IntPtr.Zero) return;
        var displays = DesktopPlacement.Displays(); if (displays.Count == 0) return;
        restoring = true;
        try {
            var current = reset ? null : IsLoaded ? DesktopPlacement.Read(handle) : prefs.Placement;
            string? monitor = reset ? displays[0].Id : IsLoaded ? null : prefs.Monitor;
            DesktopPlacement.Apply(handle, Preferences.Recover(current, displays, monitor));
        } finally { restoring = false; }
        ScheduleSave();
    }
    void Change(Preferences next) { if(rootGroup!=""&&next.Locked!=prefs.Locked)next=next.WithArrangement(rootProfile,next.ForArrangement(rootProfile) with{Locked=next.Locked});prefs = next.Validate(); ApplyAppearance(); Render(); UpdateSettings(); ScheduleSave(); }
    void ApplyAppearance() {
        surface.Background = new SolidColorBrush(Color.FromArgb((byte)(prefs.BackgroundOpacity * 255), 0, 0, 0));
        Topmost = prefs.Topmost; ResizeMode = prefs.Locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        WindowChrome.GetWindowChrome(this).ResizeBorderThickness=new Thickness(prefs.Locked?0:7);
        lockButton.Content = WindowInteraction.LockGlyph(prefs.Locked);
        Theme.Accessible(lockButton,WindowInteraction.LockAction(prefs.Locked));
        lockButton.ToolTip = WindowInteraction.LockHint(prefs.Locked);
        foreach(var view in detached.Values)UpdateDetachedAppearance(view);
    }
    void Through(bool enabled) {
        // Only enable once a live tray recovery surface exists; never persist this mode.
        if (enabled && tray?.Available != true) return;
        clickThrough = enabled; DesktopPlacement.ClickThrough(handle, enabled);foreach(var view in detached.Values)UpdateDetachedAppearance(view); UpdateConnection(); UpdateSettings();
    }
    void Reveal(bool settings = false) {
        groupHidden=false;Through(false);
        if(rootGroup!=""||detached.Count==0){if(!IsVisible)Show();if(WindowState!=WindowState.Normal)WindowState=WindowState.Normal;Recover(false);Activate();}
        foreach(var view in detached.Values){view.Show();if(view.WindowState!=WindowState.Normal)view.WindowState=WindowState.Normal;view.Recover(false);}
        if(rootGroup==""&&detached.Count>0)detached.Values.First().Activate();
        if(settings)OpenSourceControls(rootGroup!=""||detached.Count==0?this:detached.Values.First());
    }
    void CreateTray() {
        try { tray = new TrayIcon(this,BuildTrayActions); }
        catch { tray?.Dispose(); tray=null; connectionText="Tray unavailable; keep this window visible. Click-through disabled."; }
    }
    TrayAction[] BuildTrayActions(){
        var actions=new List<TrayAction>{
            new("Show overlay","\uE8A7",()=>Reveal()),new("Open Controls","\uE713",()=>Reveal(true)),
            new("Unlock caption windows",WindowInteraction.LockGlyph(true),()=>{foreach(var group in layout.Groups)prefs=prefs.WithArrangement(ArrangementKey(group.Key),SourcePlacement(group.Key) with{Locked=false});Change(prefs with{Locked=false});}),
            new("Disable click-through","\uE8B0",()=>{Through(false);Reveal();}),
            new("Restore to primary monitor","\uE9E9",()=>RestoreAll()),
        };
        if(state.Sources.ContainsKey("a")&&state.Sources.ContainsKey("b")&&layout.GroupFor("a")?.Key!="a+b")actions.Add(new("Reattach captions","\uE737",()=>Reattach()));
        actions.Add(new("Quit Rayme","\uE8BB",QuitRayme));return actions.ToArray();
    }
    async Task RunClient() {
        try { await client!.RunAsync(json => Dispatcher.InvokeAsync(() => { state.Apply(json);HandleSetup(json);HandleProcessing(json);Render();UpdateSettings(); }).Task,
            status => Dispatcher.InvokeAsync(() => { if(!quitRequested){transportConnected=status=="Connected";connectionText = status;UpdateConnection();} }).Task); }
        catch { if (!closing) { transportConnected=false;connectionText = "Caption connection failed. Restart Windows caption mode."; UpdateConnection(); } }
    }
    void UpdateConnection() {
        bool connected=transportConnected;
        bool error=state.Sources.Values.Any(s=>SourceAvailability.Read(s,state.Paused,connected).Error);
        void Bar(CaptionToolbar bar,string key,bool locked){
            var problems=key.Split('+').Where(state.Sources.ContainsKey).Select(s=>state.Sources[s]).Where(s=>SourceAvailability.Read(s,state.Paused,connected).Error).ToArray();
            bool failed=problems.Length>0||connected&&connectionText!="Connected"&&!quitRequested||!connected&&(connectionText.Contains("lost",StringComparison.OrdinalIgnoreCase)||connectionText.Contains("failed",StringComparison.OrdinalIgnoreCase)||connectionText.Contains("not sent",StringComparison.OrdinalIgnoreCase));
            string status=connectionText!="Connected"?connectionText:state.Paused?"Paused":failed?"Attention needed":"Connected";
            if(clickThrough)status="Click-through · "+status;
            string detail=connectionText!="Connected"?connectionText:failed?string.Join(" · ",problems.Select(s=>s.Source.Name+": "+s.Message)):state.Paused?"Capture paused":"Local caption session connected";
            bar.Update(status,detail,locked,PairingFor(key),failed,state.Paused);
        }
        Bar(toolbar,rootGroup,prefs.Locked);
        foreach(var view in detached.Values){view.Title="Rayme · "+string.Join(" + ",view.Slot.Split('+').Select(s=>state.Sources.GetValueOrDefault(s)?.Source.Name));Bar(view.Toolbar,view.Slot,SourcePlacement(view.Slot).Locked);}
        foreach(var panel in panels.Values)panel.Availability(state.Sources.GetValueOrDefault(panel.Slot),state.Paused,connected);
        if(backendStatus!=null){
            string message=setupRequired&&setupState is {} setup?setup.GetProperty("message").GetString()??"Complete speech setup.":connectionText=="Connected"?state.Status:connectionText;backendStatus.Text=message;
            bool noticeError=error||message.Contains("failed",StringComparison.OrdinalIgnoreCase)||message.Contains("could not",StringComparison.OrdinalIgnoreCase)||message.Contains("unavailable",StringComparison.OrdinalIgnoreCase);
            statusCard!.Background=Theme.Brush(noticeError?"#242424":"#202020");
        }
    }
    void Render() {
        var slots=TranscriptPresentation.Slots(state);ArrangePanels(slots);
        foreach(string slot in panels.Keys.Except(slots).ToArray())panels[slot].Update(state,prefs.FontSize,prefs.ColorFor(slot),setupRequired);
        foreach(string slot in slots){var group=layout.GroupFor(slot)!;panels[slot].Interactions(group.Key==rootGroup?prefs.Locked:SourcePlacement(group.Key).Locked);panels[slot].Update(state,prefs.FontSize,prefs.ColorFor(slot),setupRequired);}
        UpdateConnection();
    }
    async Task Send(object command)=>_ = await TrySend(command);
    async Task<bool> TrySend(object command) {
#if !RAYME_DISTRIBUTION
        if(smoke){smokeCommands.Add(JsonSerializer.Serialize(command));return true;}
#endif
        if(client!=null&&await client.SendAsync(command)){transportConnected=true;if(connectionText.StartsWith("Control not sent",StringComparison.Ordinal))connectionText="Connected";return true;}
        connectionText="Control not sent — connection unavailable or busy. Please retry.";UpdateConnection();return false;
    }
    void UpdateSettings() {
        if(discoveryRevision!=state.ApplicationsRevision){discoveryRevision=state.ApplicationsRevision;discoveryPending=false;}
        bool removed=false;
        foreach(string slot in removingSlots.Where(s=>!state.Sources.ContainsKey(s)).ToArray()){
            removingSlots.Remove(slot);selectionDrafts.Remove(slot);prefs=prefs with{VisibleApplications=prefs.VisibleApplications.Where(s=>s!=slot).ToArray()};removed=true;
        }
        if(removed){BuildAudioCards();ScheduleSave();}
        var visible=ApplicationSlots.All.Where(s=>prefs.VisibleApplications.Contains(s)||state.Sources.ContainsKey(s)&&!removingSlots.Contains(s)).ToArray();
        if(!visible.SequenceEqual(prefs.VisibleApplications)){prefs=prefs with{VisibleApplications=visible};BuildAudioCards();ScheduleSave();}
        if (controls == null) return;
        syncing = true;
        try {
            UpdateConnection();
            foreach (var (slot,pair) in selectors) {
                var choices = slot == "device" ? new[] { new Choice("default","Windows default multimedia output") }.Concat(state.Devices).ToList() : state.Apps.ToList();
                var active = state.Sources.GetValueOrDefault(slot)?.Source;
                string? selected = active?.Identity ?? (pair.Select.SelectedItem as Choice)?.Id ?? selectionDrafts.GetValueOrDefault(slot)?.Id;
                if (selected != null && !choices.Any(c => c.Id == selected)) choices.Add(new(selected,"Selected process unavailable — refresh/reselect"));
                var previous = pair.Select.ItemsSource as List<Choice>;
                if (previous == null || !previous.SequenceEqual(choices)) { pair.Select.ItemsSource = choices; pair.Select.SelectedValue = selected; }
                else if (active != null) pair.Select.SelectedValue = selected;
                pair.Enabled.IsChecked = active != null; pair.Enabled.IsEnabled = !removingSlots.Contains(slot)&&(slot == "device" || state.ProcessSupported); pair.Select.IsEnabled = pair.Enabled.IsEnabled;
            }
            pauseButton!.Content = state.Paused ? "Resume capture" : "Pause capture";
            Theme.Accessible(pauseButton,state.Paused?"Resume capture":"Pause capture");
            sourceStatus!.Text = string.Join("\n",state.Sources.Values.Select(s => $"{s.Source.Id.ToUpperInvariant()} · {s.Source.Name}: {s.Label} — {s.Message}"+(state.AudioLevels.TryGetValue(s.Source.Id,out var level)?$"\nSignal: {level.Dbfs:0} dBFS · "+(level.Rms==0?"silence":level.Low?"very quiet — increase application playback volume if speech is missing":"audio detected"):"" )));
            if (lockSetting != null){lockSetting.IsChecked = prefs.Locked;lockSetting.Visibility=rootGroup!=""||detached.Count==0?Visibility.Visible:Visibility.Collapsed;}
            foreach(var (slot,check) in sourceLocks){
                var group=layout.GroupFor(slot);check.Visibility=group!=null&&group.Key!=rootGroup&&group.Slots[0]==slot?Visibility.Visible:Visibility.Collapsed;check.IsChecked=SourcePlacement(group?.Key??slot).Locked;
                if(group!=null&&check.Content is StackPanel content&&content.Children[0] is TextBlock title)title.Text="Lock "+string.Join(" + ",group.Slots.Select(s=>state.Sources[s].Source.Name))+" window";
            }
            foreach(var(slot,section)in colorSections)section.Visibility=slot=="device"||prefs.VisibleApplications.Contains(slot)?Visibility.Visible:Visibility.Collapsed;
            foreach(var(slot,title)in colorTitles)title.Text=state.Sources.GetValueOrDefault(slot)?.Source.Name??(slot=="device"?"Windows playback":"Source "+slot.ToUpperInvariant());
            foreach(var(slot,button)in customColorButtons){string color=prefs.ColorFor(slot);button.Content="Custom color… "+color;}
            if (topSetting != null) topSetting.IsChecked = prefs.Topmost;
            if (throughSetting != null) throughSetting.IsChecked = clickThrough;
            if(fontControl!=null)fontControl.Value=prefs.FontSize;
            if(opacityControl!=null)opacityControl.Value=prefs.BackgroundOpacity;
            foreach(var (slot,swatches) in colorControls)foreach(var swatch in swatches)swatch.IsChecked=(string)swatch.Tag==(prefs.ColorFor(slot));
        } finally { syncing = false; }
    }
}
