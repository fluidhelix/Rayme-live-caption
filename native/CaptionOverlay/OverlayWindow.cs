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
    bool groupHidden;
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
        client = session; smoke = isSmoke; prefs = smoke ? new() : Preferences.Load(settingsFile);
        Icon = new BitmapImage(Theme.IconUri); Title = "Rayme Smart Live Captions"; Width = 640; Height = 360; MinWidth = 300; MinHeight = 180;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip; AllowsTransparency = true;
        Background = Brushes.Transparent; Foreground = Brushes.White; FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        ShowInTaskbar = true; ShowActivated = false; Topmost = prefs.Topmost;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(7), CornerRadius = new CornerRadius(14), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        toolbar=new CaptionToolbar(HideGroup,()=>{if(layout.Detached)Reattach();else Detach();},()=>Change(prefs with{Locked=!prefs.Locked}),()=>OpenSourceControls(this));
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
        Closing += (_,e) => { if(!smoke&&!backendClosed){e.Cancel=true;QuitRayme();return;} closing = true; saveTimer.Stop(); displayTimer.Stop(); if (!smoke) Save(); };
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
        if (handle != IntPtr.Zero && WindowState == WindowState.Normal) prefs = prefs with { Placement = DesktopPlacement.Read(handle), Monitor = DesktopPlacement.MonitorId(handle) };
        foreach(var view in detached.Values)prefs=prefs.WithSource(view.Slot,view.Placement());
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
    void Change(Preferences next) { prefs = next.Validate(); ApplyAppearance(); Render(); UpdateSettings(); ScheduleSave(); }
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
        if(layout.Detached){foreach(var view in detached.Values){view.Show();if(view.WindowState!=WindowState.Normal)view.WindowState=WindowState.Normal;view.Recover(false);}detached.Values.First().Activate();}
        else{if(!IsVisible)Show();if(WindowState!=WindowState.Normal)WindowState=WindowState.Normal;Recover(false);Activate();}
        if(settings)OpenSourceControls(layout.Detached?detached.Values.First():this);
    }
    void CreateTray() {
        try { tray = new TrayIcon(this,BuildTrayActions); }
        catch { tray?.Dispose(); tray=null; connectionText="Tray unavailable; keep this window visible. Click-through disabled."; }
    }
    TrayAction[] BuildTrayActions(){
        var actions=new List<TrayAction>{
            new("Show overlay","\uE8A7",()=>Reveal()),new("Open Controls","\uE713",()=>Reveal(true)),
            new(layout.Detached?"Unlock both windows":prefs.Locked?"Unlock position":"Lock position",WindowInteraction.LockGlyph(layout.Detached?prefs.DetachedA.Locked&&prefs.DetachedB.Locked:prefs.Locked),()=>{if(layout.Detached){Change(prefs with{DetachedA=prefs.DetachedA with{Locked=false},DetachedB=prefs.DetachedB with{Locked=false}});}else Change(prefs with{Locked=!prefs.Locked});}),
            new("Disable click-through","\uE8B0",()=>{Through(false);Reveal();}),
            new("Restore to primary monitor","\uE9E9",()=>RestoreAll()),
        };
        if(layout.Detached)actions.Add(new("Reattach captions","\uE737",()=>Reattach()));
        actions.Add(new("Quit Rayme","\uE8BB",QuitRayme));return actions.ToArray();
    }
    async Task RunClient() {
        try { await client!.RunAsync(json => Dispatcher.InvokeAsync(() => { state.Apply(json);HandleSetup(json);HandleProcessing(json);Render();UpdateSettings(); }).Task,
            status => Dispatcher.InvokeAsync(() => { if(!quitRequested){connectionText = status;UpdateConnection();} }).Task); }
        catch { if (!closing) { connectionText = "Caption connection failed. Restart Windows caption mode."; UpdateConnection(); } }
    }
    void UpdateConnection() {
        string Detail(IEnumerable<string> slots)=>string.Join(" · ",slots.Where(state.Sources.ContainsKey).Select(slot=>{var s=state.Sources[slot];return $"{s.Source.Name}: {s.Label} — {s.Message}";}));
        string Short(IEnumerable<string> slots)=>connectionText!="Connected"?connectionText:string.Join(" · ",slots.Where(state.Sources.ContainsKey).Select(slot=>{var s=state.Sources[slot];return $"{s.Source.Name}: {s.Label}";}));
        var slots=TranscriptPresentation.Slots(state);
        bool error=state.Sources.Values.Any(s=>s.HasFailure)||connectionText.Contains("lost",StringComparison.OrdinalIgnoreCase)||connectionText.Contains("failed",StringComparison.OrdinalIgnoreCase)||connectionText.Contains("not sent",StringComparison.OrdinalIgnoreCase);
        string status=Short(slots);if(status.Length==0)status=state.Paused?"Capture paused":"Ready — choose an audio source";
        if(state.Paused)status="Paused · "+status;
        if(clickThrough)status="Click-through · "+status;
        toolbar.Update(status,connectionText+" · "+Detail(slots),prefs.Locked,false,CaptionLayout.CanDetach(state),error,state.Paused);
        foreach(var view in detached.Values){view.Title="Rayme · "+state.Sources.GetValueOrDefault(view.Slot)?.Source.Name;view.Toolbar.Update((state.Paused?"Paused · ":"")+Short(new[]{view.Slot}),connectionText+" · "+Detail(new[]{view.Slot}),SourcePlacement(view.Slot).Locked,true,true,error,state.Paused);}
        if(backendStatus!=null){
            string message=setupRequired&&setupState is {} setup?setup.GetProperty("message").GetString()??"Complete speech setup.":connectionText=="Connected"?state.Status:connectionText;backendStatus.Text=message;
            bool noticeError=error||message.Contains("failed",StringComparison.OrdinalIgnoreCase)||message.Contains("could not",StringComparison.OrdinalIgnoreCase)||message.Contains("unavailable",StringComparison.OrdinalIgnoreCase);
            statusCard!.Background=Theme.Brush(noticeError?"#242424":"#202020");
        }
    }
    void Render() {
        UpdateConnection();
        if(layout.Detached&&!CaptionLayout.CanDetach(state))Reattach(!groupHidden&&!quitRequested);
        var slots=TranscriptPresentation.Slots(state);string signature=string.Join(",",slots)+(layout.Detached?":detached":"");
        if(panelLayout!=signature){
            foreach(var panel in panels.Values)panel.PrepareMove();
            captions.Children.Clear();captions.RowDefinitions.Clear();foreach(var view in detached.Values)view.Body.Children.Clear();panelLayout=signature;
            if(slots.Length==0){captions.Children.Add(new TextBlock{Text=setupRequired?"Complete speech setup in Controls.":"Choose an audio source in Controls.",Margin=new(18),Foreground=Theme.Brush("#C0C0C0"),TextWrapping=TextWrapping.Wrap});}
            for(int i=0;i<slots.Length;i++){
                string slot=slots[i];if(!panels.TryGetValue(slot,out var panel))panels[slot]=panel=new TranscriptPanel(slot,smoke);
                panel.BorderThickness=new(0,!layout.Detached&&i>0?1:0,0,0);panel.BorderBrush=Theme.Brush("#292929");
                if(layout.Detached)detached[slot].Body.Children.Add(panel);
                else{captions.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});Grid.SetRow(panel,i);captions.Children.Add(panel);}
            }
        }
        foreach(string slot in panels.Keys.Except(slots).ToArray())panels[slot].Update(state,prefs.FontSize,slot=="a"?prefs.ColorA:slot=="b"?prefs.ColorB:prefs.ColorDevice,setupRequired);
        foreach(string slot in slots){string color=slot=="a"?prefs.ColorA:slot=="b"?prefs.ColorB:prefs.ColorDevice;panels[slot].Interactions(layout.Detached?SourcePlacement(slot).Locked:prefs.Locked);panels[slot].Update(state,prefs.FontSize,color,setupRequired);}
        UpdateConnection();
    }
    async Task Send(object command) {
#if !RAYME_DISTRIBUTION
        if(smoke){smokeCommands.Add(JsonSerializer.Serialize(command));return;}
#endif
        if (client == null || !await client.SendAsync(command)) { connectionText = "Control not sent — connection unavailable or busy. Please retry."; UpdateConnection(); }
    }
    void UpdateSettings() {
        if (controls == null) return;
        syncing = true;
        try {
            UpdateConnection();
            foreach (var (slot,pair) in selectors) {
                var choices = slot == "device" ? new[] { new Choice("default","Windows default multimedia output") }.Concat(state.Devices).ToList() : state.Apps.ToList();
                var active = state.Sources.GetValueOrDefault(slot)?.Source;
                string? selected = active?.Identity ?? (pair.Select.SelectedItem as Choice)?.Id;
                if (selected != null && !choices.Any(c => c.Id == selected)) choices.Add(new(selected,"Selected process unavailable — refresh/reselect"));
                var previous = pair.Select.ItemsSource as List<Choice>;
                if (previous == null || !previous.SequenceEqual(choices)) { pair.Select.ItemsSource = choices; pair.Select.SelectedValue = selected; }
                else if (active != null) pair.Select.SelectedValue = selected;
                pair.Enabled.IsChecked = active != null; pair.Enabled.IsEnabled = slot == "device" || state.ProcessSupported; pair.Select.IsEnabled = pair.Enabled.IsEnabled;
            }
            pauseButton!.Content = state.Paused ? "Resume capture" : "Pause capture";
            Theme.Accessible(pauseButton,state.Paused?"Resume capture":"Pause capture");
            sourceStatus!.Text = string.Join("\n",state.Sources.Values.Select(s => $"{s.Source.Id.ToUpperInvariant()} · {s.Source.Name}: {s.Label} — {s.Message}"+(state.AudioLevels.TryGetValue(s.Source.Id,out var level)?$"\nSignal: {level.Dbfs:0} dBFS · "+(level.Rms==0?"silence":level.Low?"very quiet — increase application playback volume if speech is missing":"audio detected"):"" )));
            if (lockSetting != null){lockSetting.IsChecked = prefs.Locked;lockSetting.Visibility=layout.Detached?Visibility.Collapsed:Visibility.Visible;}
            foreach(var (slot,check) in sourceLocks){check.Visibility=layout.Detached?Visibility.Visible:Visibility.Collapsed;check.IsChecked=SourcePlacement(slot).Locked;}
            foreach(var(slot,title)in colorTitles)title.Text=state.Sources.GetValueOrDefault(slot)?.Source.Name??(slot=="device"?"Windows playback":"Source "+slot.ToUpperInvariant());
            foreach(var(slot,button)in customColorButtons){string color=slot=="a"?prefs.ColorA:slot=="b"?prefs.ColorB:prefs.ColorDevice;button.Content="Custom color… "+color;}
            if (topSetting != null) topSetting.IsChecked = prefs.Topmost;
            if (throughSetting != null) throughSetting.IsChecked = clickThrough;
            if(fontControl!=null)fontControl.Value=prefs.FontSize;
            if(opacityControl!=null)opacityControl.Value=prefs.BackgroundOpacity;
            foreach(var (slot,swatches) in colorControls)foreach(var swatch in swatches)swatch.IsChecked=(string)swatch.Tag==(slot=="a"?prefs.ColorA:slot=="b"?prefs.ColorB:prefs.ColorDevice);
        } finally { syncing = false; }
    }
}
