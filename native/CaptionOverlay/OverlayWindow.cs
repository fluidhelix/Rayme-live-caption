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
    readonly Border surface = new() { CornerRadius = new CornerRadius(16), BorderThickness = new Thickness(1), BorderBrush = Brush("#47566C"), Padding = new Thickness(16,8,16,12) };
    readonly Grid captions = new();
    readonly TextBlock connection = new() { FontSize = 12, Foreground = Brush("#A8E5D0"), Margin = new Thickness(0,2,0,4), TextWrapping = TextWrapping.Wrap };
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
    bool quitRequested, backendClosed;
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
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new());
        var header = new DockPanel { LastChildFill = true };
        var hide = Theme.IconButton("\uE738", "Hide overlay", (_,_) => { if (tray?.Available == true) Hide(); }); hide.ToolTip = "Hide to notification area; use the Rayme tray icon to restore"; DockPanel.SetDock(hide,Dock.Right); header.Children.Add(hide);
        settingsButton = Theme.IconButton("\uE713", "Open Controls", (_,_) => OpenControls()); DockPanel.SetDock(settingsButton,Dock.Right); header.Children.Add(settingsButton);
        lockButton = Theme.IconButton("\uE72E", "Lock overlay position", (_,_) => Change(prefs with { Locked = !prefs.Locked })); DockPanel.SetDock(lockButton,Dock.Right); header.Children.Add(lockButton);
        // Header controls always belong to WPF client hit testing, including after
        // WindowChrome rebuilds its non-client region on a ResizeMode change.
        WindowChrome.SetIsHitTestVisibleInChrome(header,true);
        var drag = new StackPanel { Orientation=Orientation.Horizontal, VerticalAlignment=VerticalAlignment.Center, Cursor=Cursors.SizeAll, ToolTip="Drag to any monitor (unlock first)", Margin=new Thickness(0,4,0,4) };
        drag.Children.Add(Theme.Mark(22)); var wordmark=Theme.Text("Rayme",13,"#E0E7F2",true); wordmark.Margin=new Thickness(8,0,0,0); wordmark.VerticalAlignment=VerticalAlignment.Center; drag.Children.Add(wordmark);
        drag.MouseLeftButtonDown += (_,e) => { if (!prefs.Locked && !clickThrough && e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } } }; header.Children.Add(drag);
        grid.Children.Add(header); Grid.SetRow(connection,1); grid.Children.Add(connection);
        Grid.SetRow(captions,2); grid.Children.Add(captions); surface.Child = grid; Content = surface;
        saveTimer.Tick += (_,_) => { saveTimer.Stop(); Save(); };
        displayTimer.Tick += (_,_) => { displayTimer.Stop(); Recover(false); };
        SourceInitialized += (_,_) => {
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
            Recover(false); ApplyAppearance();
        };
        LocationChanged += (_,_) => ScheduleSave(); SizeChanged += (_,_) => ScheduleSave();
        Loaded += async (_,_) => {
#if !RAYME_DISTRIBUTION
            if (smoke) { await SmokeAsync(); return; }
#endif
            CreateTray(); ApplyAppearance(); Render();
            Console.Out.WriteLine("{\"type\":\"overlay-ready\"}"); // Control acknowledgement only; no credentials or captions.
            if (client != null) await RunClient();
        };
        Closing += (_,e) => { if(!smoke&&!backendClosed){e.Cancel=true;QuitRayme();return;} closing = true; saveTimer.Stop(); displayTimer.Stop(); if (!smoke) Save(); };
        Closed += async (_,_) => {
            controls?.Close(); tray?.Dispose(); if (client != null) await client.DisposeAsync();
        };
    }
    IntPtr WindowMessage(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled) {
        if (message is 0x007E or 0x02E0 or 0x001A) { displayTimer.Stop(); displayTimer.Start(); } // display, DPI, work-area changes
        return IntPtr.Zero;
    }
    void ScheduleSave() { if (restoring || closing || handle == IntPtr.Zero || smoke) return; saveTimer.Stop(); saveTimer.Start(); }
    void Save() {
        if (handle != IntPtr.Zero && WindowState == WindowState.Normal) prefs = prefs with { Placement = DesktopPlacement.Read(handle), Monitor = DesktopPlacement.MonitorId(handle) };
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
        surface.Background = new SolidColorBrush(Color.FromArgb((byte)(prefs.BackgroundOpacity * 255), 15, 23, 38));
        Topmost = prefs.Topmost; ResizeMode = prefs.Locked ? ResizeMode.NoResize : ResizeMode.CanResizeWithGrip;
        WindowChrome.GetWindowChrome(this).ResizeBorderThickness=new Thickness(prefs.Locked?0:7);
        lockButton.Content = WindowInteraction.LockGlyph(prefs.Locked);
        Theme.Accessible(lockButton,WindowInteraction.LockAction(prefs.Locked));
        lockButton.ToolTip = WindowInteraction.LockHint(prefs.Locked);
    }
    void Through(bool enabled) {
        // Only enable once a live tray recovery surface exists; never persist this mode.
        if (enabled && tray?.Available != true) return;
        clickThrough = enabled; DesktopPlacement.ClickThrough(handle, enabled); UpdateConnection(); UpdateSettings();
    }
    void Reveal(bool settings = false) {
        Through(false); if (!IsVisible) Show(); if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        Recover(false); Activate(); if (settings) OpenControls();
    }
    void CreateTray() {
        try { tray = new TrayIcon(this,BuildTrayActions); }
        catch { tray?.Dispose(); tray=null; connectionText="Tray unavailable; keep this window visible. Click-through disabled."; }
    }
    TrayAction[] BuildTrayActions() => [
        new("Show overlay", "\uE8A7", () => Reveal()),
        new("Open Controls", "\uE713", () => Reveal(true)),
        new(prefs.Locked ? "Unlock position" : "Lock position", WindowInteraction.LockGlyph(prefs.Locked), () => Change(prefs with { Locked=!prefs.Locked })),
        new("Disable click-through", "\uE8B0", () => { Through(false); Reveal(); }),
        new("Restore to primary monitor", "\uE9E9", () => { Change(prefs with { Locked=false }); Reveal(); Recover(true); }),
        new("Quit Rayme", "\uE8BB", QuitRayme)
    ];
    async Task RunClient() {
        try { await client!.RunAsync(json => Dispatcher.InvokeAsync(() => { state.Apply(json);HandleSetup(json);HandleProcessing(json);Render();UpdateSettings(); }).Task,
            status => Dispatcher.InvokeAsync(() => { if(!quitRequested){connectionText = status;UpdateConnection();} }).Task); }
        catch { if (!closing) { connectionText = "Caption connection failed. Restart Windows caption mode."; UpdateConnection(); } }
    }
    void UpdateConnection() {
        string health=connectionText=="Connected"?string.Join(" · ",state.Sources.Values.Select(s=>$"{s.Source.Id.ToUpperInvariant()}: {s.Label}"+((s.HasFailure||s.State is "recovering" or "delayed")?$" — {s.Message}":""))):"";
        connection.Text = "●  " + connectionText + (health.Length>0?" · "+health:"") + (state.Paused ? " · Capture paused" : "") + (clickThrough ? " · Click-through: use tray to recover" : "");
        bool error=state.Sources.Values.Any(s=>s.HasFailure)||connectionText.Contains("lost",StringComparison.OrdinalIgnoreCase)||connectionText.Contains("failed",StringComparison.OrdinalIgnoreCase)||connectionText.Contains("not sent",StringComparison.OrdinalIgnoreCase);
        connection.Foreground=Theme.Brush(error?"#FFD0D5":state.Paused||connectionText.StartsWith("Connecting",StringComparison.Ordinal)?"#FBE0A7":"#BBE6D8");
        if(backendStatus!=null){
            string message=setupRequired&&setupState is {} setup?setup.GetProperty("message").GetString()??"Complete speech setup.":connectionText=="Connected"?state.Status:connectionText;backendStatus.Text=message;
            bool noticeError=error||message.Contains("failed",StringComparison.OrdinalIgnoreCase)||message.Contains("could not",StringComparison.OrdinalIgnoreCase)||message.Contains("unavailable",StringComparison.OrdinalIgnoreCase);
            statusCard!.Background=Theme.Brush(noticeError?"#3A2431":"#1B2D3D");
        }
    }
    void Render() {
        UpdateConnection();
        var slots=TranscriptPresentation.Slots(state);string layout=string.Join(",",slots);
        if(panelLayout!=layout){
            captions.Children.Clear();captions.RowDefinitions.Clear();panelLayout=layout;
            for(int i=0;i<slots.Length;i++){
                string slot=slots[i];if(!panels.TryGetValue(slot,out var panel))panels[slot]=panel=new TranscriptPanel(slot);
                captions.RowDefinitions.Add(new(){Height=new GridLength(1,GridUnitType.Star)});Grid.SetRow(panel,i);captions.Children.Add(panel);
            }
        }
        foreach(string slot in slots){string color=slot=="a"?prefs.ColorA:slot=="b"?prefs.ColorB:prefs.ColorDevice;panels[slot].Update(TranscriptPresentation.Build(state,slot),prefs.FontSize,color,setupRequired);}
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
            if (lockSetting != null) lockSetting.IsChecked = prefs.Locked;
            if (topSetting != null) topSetting.IsChecked = prefs.Topmost;
            if (throughSetting != null) throughSetting.IsChecked = clickThrough;
            if(fontControl!=null)fontControl.Value=prefs.FontSize;
            if(opacityControl!=null)opacityControl.Value=prefs.BackgroundOpacity;
            foreach(var (slot,swatches) in colorControls)foreach(var swatch in swatches)swatch.IsChecked=(string)swatch.Tag==(slot=="a"?prefs.ColorA:slot=="b"?prefs.ColorB:prefs.ColorDevice);
        } finally { syncing = false; }
    }
    sealed class TranscriptPanel : Border {
        readonly TextBlock label=Theme.Text("",13,strong:true);
        internal readonly TextBlock Text=new(){TextWrapping=TextWrapping.Wrap,Foreground=Brush("#F8FAFC"),FontStyle=FontStyles.Normal,FontWeight=FontWeights.Normal,Margin=new Thickness(0,6,6,0)};
        readonly System.Windows.Documents.Run finalRun=new(),partialRun=new();
        readonly TextBlock live=Theme.Text("LIVE",10,"#BBE6FF",true);
        internal readonly ScrollViewer Scroll=new(){VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Focusable=true};
        readonly ScrollFollow follow=new();
        string accent="";
        bool scrollPending;
        long scrollRevision;
        double savedOffset;
        internal bool IsLive=>live.Visibility==Visibility.Visible;
        internal string SourceLabel=>label.Text;
        internal string FinalText=>finalRun.Text;
        internal string PartialText=>partialRun.Text;
        public TranscriptPanel(string slot){
            BorderThickness=new Thickness(3,0,0,0);Padding=new Thickness(10,3,0,3);Margin=new Thickness(0,3,0,3);
            var grid=new Grid();grid.RowDefinitions.Add(new(){Height=GridLength.Auto});grid.RowDefinitions.Add(new());
            var header=new DockPanel();live.Width=40;live.VerticalAlignment=VerticalAlignment.Center;DockPanel.SetDock(live,Dock.Right);header.Children.Add(live);
            label.TextTrimming=TextTrimming.CharacterEllipsis;label.TextWrapping=TextWrapping.NoWrap;header.Children.Add(label);grid.Children.Add(header);
            Text.Inlines.Add(finalRun);Text.Inlines.Add(partialRun);Scroll.Content=Text;Scroll.SetResourceReference(Control.FocusVisualStyleProperty,"KeyboardFocus");Theme.Accessible(Scroll,slot=="device"?"Playback transcript":"Application "+slot.ToUpperInvariant()+" transcript");
            Scroll.ScrollChanged+=(_,e)=>{if(e.ExtentHeightChange==0&&e.ViewportHeightChange==0)follow.ReaderMoved(Scroll.VerticalOffset,Scroll.ScrollableHeight);};
            Grid.SetRow(Scroll,1);grid.Children.Add(Scroll);Child=grid;
        }
        public void Update(Transcript transcript,double size,string color,bool setup){
            label.Text=transcript.Label;label.ToolTip=transcript.Label;live.Visibility=transcript.Live?Visibility.Visible:Visibility.Hidden;
            string final=transcript.FinalText,partial=transcript.PartialText;
            if(final.Length==0&&partial.Length==0)final=setup?"Complete speech setup to begin.":"Listening for conversation…";
            string tail=partial.Length>0?(transcript.FinalText.Length>0?" ":"")+partial:"";
            bool changed=finalRun.Text!=final||partialRun.Text!=tail||Text.FontSize!=size;
            if(finalRun.Text!=final)finalRun.Text=final;if(partialRun.Text!=tail)partialRun.Text=tail;
            Text.FontSize=size;Text.LineHeight=size*1.4;if(accent!=color){BorderBrush=Theme.Brush(color);accent=color;}
            if(changed){
                scrollRevision=follow.Revision;savedOffset=Scroll.VerticalOffset;
                if(!scrollPending){scrollPending=true;Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(()=>{
                    scrollPending=false;if(follow.Revision!=scrollRevision)return; // A reader moved after this update; leave their position alone.
                    if(follow.Following)Scroll.ScrollToEnd();else Scroll.ScrollToVerticalOffset(savedOffset);
                }));}
            }
        }
    }
}
