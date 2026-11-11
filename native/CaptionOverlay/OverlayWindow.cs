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
    readonly StackPanel captions = new();
    readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly TextBlock connection = new() { FontSize = 12, Foreground = Brush("#A8E5D0"), Margin = new Thickness(0,2,0,4), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock waiting = new() { Text = "Waiting for conversation…\nOpen Controls to enable applications.", Foreground = Brush("#E0E7F2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4,12,4,4) };
    readonly Dictionary<string, CaptionView> views = [];
    readonly Button lockButton;
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
        var settings = Theme.IconButton("\uE713", "Open Controls", (_,_) => OpenControls()); DockPanel.SetDock(settings,Dock.Right); header.Children.Add(settings);
        lockButton = Theme.IconButton("\uE72E", "Lock overlay position", (_,_) => Change(prefs with { Locked = !prefs.Locked })); DockPanel.SetDock(lockButton,Dock.Right); header.Children.Add(lockButton);
        var drag = new StackPanel { Orientation=Orientation.Horizontal, VerticalAlignment=VerticalAlignment.Center, Cursor=Cursors.SizeAll, ToolTip="Drag to any monitor (unlock first)", Margin=new Thickness(0,4,0,4) };
        drag.Children.Add(Theme.Mark(22)); var wordmark=Theme.Text("Rayme",13,"#E0E7F2",true); wordmark.Margin=new Thickness(8,0,0,0); wordmark.VerticalAlignment=VerticalAlignment.Center; drag.Children.Add(wordmark);
        drag.MouseLeftButtonDown += (_,e) => { if (!prefs.Locked && !clickThrough && e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } } }; header.Children.Add(drag);
        grid.Children.Add(header); Grid.SetRow(connection,1); grid.Children.Add(connection);
        scroll.Content = captions;scroll.Focusable=true;scroll.SetResourceReference(Control.FocusVisualStyleProperty,"KeyboardFocus");Theme.Accessible(scroll,"Caption history");Grid.SetRow(scroll,2); grid.Children.Add(scroll); surface.Child = grid; Content = surface;
        saveTimer.Tick += (_,_) => { saveTimer.Stop(); Save(); };
        displayTimer.Tick += (_,_) => { displayTimer.Stop(); Recover(false); };
        SourceInitialized += (_,_) => {
            handle = new WindowInteropHelper(this).Handle;
            HwndSource.FromHwnd(handle)?.AddHook(WindowMessage);
            Recover(false); ApplyAppearance();
        };
        LocationChanged += (_,_) => ScheduleSave(); SizeChanged += (_,_) => ScheduleSave();
        Loaded += async (_,_) => {
            if (smoke) { await SmokeAsync(); return; }
            CreateTray(); ApplyAppearance(); Render();
            Console.Out.WriteLine("{\"type\":\"overlay-ready\"}"); // Control acknowledgement only; no credentials or captions.
            if (client != null) await RunClient();
        };
        Closing += (_,_) => { closing = true; saveTimer.Stop(); displayTimer.Stop(); if (!smoke) Save(); };
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
        lockButton.Content = prefs.Locked ? "\uE785" : "\uE72E";
        Theme.Accessible(lockButton,prefs.Locked ? "Unlock overlay position" : "Lock overlay position");
        lockButton.ToolTip = prefs.Locked ? "Position and size locked" : "Lock position and size";
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
        new(prefs.Locked ? "Unlock position" : "Lock position", "\uE72E", () => Change(prefs with { Locked=!prefs.Locked })),
        new("Disable click-through", "\uE8B0", () => { Through(false); Reveal(); }),
        new("Restore to primary monitor", "\uE9E9", () => { Change(prefs with { Locked=false }); Reveal(); Recover(true); }),
        new("Exit overlay", "\uE8BB", Close)
    ];
    async Task RunClient() {
        try { await client!.RunAsync(json => Dispatcher.InvokeAsync(() => { state.Apply(json); Render(); UpdateSettings(); }).Task,
            status => Dispatcher.InvokeAsync(() => { connectionText = status; UpdateConnection(); }).Task); }
        catch { if (!closing) { connectionText = "Caption connection failed. Restart Windows caption mode."; UpdateConnection(); } }
    }
    void UpdateConnection() {
        connection.Text = "●  " + connectionText + (state.Paused ? " · Capture paused" : "") + (clickThrough ? " · Click-through: use tray to recover" : "");
        bool error=connectionText.Contains("lost",StringComparison.OrdinalIgnoreCase)||connectionText.Contains("failed",StringComparison.OrdinalIgnoreCase)||connectionText.Contains("not sent",StringComparison.OrdinalIgnoreCase);
        connection.Foreground=Theme.Brush(error?"#FFD0D5":state.Paused||connectionText.StartsWith("Connecting",StringComparison.Ordinal)?"#FBE0A7":"#BBE6D8");
        if(backendStatus!=null){
            string message=connectionText=="Connected"?state.Status:connectionText;backendStatus.Text=message;
            bool noticeError=error||message.Contains("failed",StringComparison.OrdinalIgnoreCase)||message.Contains("could not",StringComparison.OrdinalIgnoreCase)||message.Contains("unavailable",StringComparison.OrdinalIgnoreCase);
            statusCard!.Background=Theme.Brush(noticeError?"#3A2431":"#1B2D3D");
        }
    }
    void Render() {
        UpdateConnection();
        bool follow = scroll.ScrollableHeight - scroll.VerticalOffset < 60;
        var desired = state.History.Concat(state.Partials.OrderBy(x => x.Key).Select(x => x.Value)).Where(x => x.Text.Length > 0).OrderBy(x=>x.CapturedAt).ToArray();
        var keys = desired.Select(CaptionPresentation.RowKey).ToHashSet();
        foreach (string key in views.Keys.Where(x => !keys.Contains(x)).ToArray()) { captions.Children.Remove(views[key]); views.Remove(key); }
        captions.Children.Remove(waiting);
        for (int i = 0; i < desired.Length; i++) {
            var c = desired[i];
            if (!views.TryGetValue(CaptionPresentation.RowKey(c), out var view)) { view = new CaptionView(); views[CaptionPresentation.RowKey(c)] = view; }
            string color = c.Source.Id == "a" ? prefs.ColorA : c.Source.Id == "b" ? prefs.ColorB : prefs.ColorDevice;
            view.Update(c,prefs.FontSize,color);
            if (captions.Children.IndexOf(view) != i) { captions.Children.Remove(view); captions.Children.Insert(i,view); }
        }
        if (desired.Length == 0) captions.Children.Add(waiting);
        if (follow) scroll.ScrollToEnd();
    }
    async Task Send(object command) {
        if(smoke){smokeCommands.Add(JsonSerializer.Serialize(command));return;}
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
            sourceStatus!.Text = string.Join("\n",state.Sources.Values.Select(s => $"{s.Source.Id.ToUpperInvariant()} · {s.Source.Name}: {s.State}"));
            if (lockSetting != null) lockSetting.IsChecked = prefs.Locked;
            if (topSetting != null) topSetting.IsChecked = prefs.Topmost;
            if (throughSetting != null) throughSetting.IsChecked = clickThrough;
            if(fontControl!=null)fontControl.Value=prefs.FontSize;
            if(opacityControl!=null)opacityControl.Value=prefs.BackgroundOpacity;
            foreach(var (slot,swatches) in colorControls)foreach(var swatch in swatches)swatch.IsChecked=(string)swatch.Tag==(slot=="a"?prefs.ColorA:slot=="b"?prefs.ColorB:prefs.ColorDevice);
        } finally { syncing = false; }
    }
    sealed class CaptionView : Border {
        readonly TextBlock label = new() { FontSize=13, FontWeight=FontWeights.SemiBold, Foreground=Brush("#E0E7F2"), TextTrimming=TextTrimming.CharacterEllipsis };
        internal readonly TextBlock Text = new() { TextWrapping=TextWrapping.Wrap, Foreground=Brush("#F8FAFC"), Margin=new Thickness(0,5,0,0), FontStyle=FontStyles.Normal, FontWeight=FontWeights.Normal, LineStackingStrategy=LineStackingStrategy.BlockLineHeight };
        readonly Border live;
        string accent="";
        internal bool IsLive=>live.Visibility==Visibility.Visible;
        internal string SourceLabel=>label.Text;
        public CaptionView() {
            BorderThickness=new Thickness(3,0,0,0); Padding=new Thickness(12,2,3,3); Margin=new Thickness(0,9,0,10);
            var panel=new StackPanel();var header=new DockPanel { MinHeight=22 };
            live=new Border { Width=38, Height=18, CornerRadius=new CornerRadius(5), Background=Brush("#233C50"), Margin=new Thickness(8,0,0,0), Child=Theme.Text("LIVE",10,"#BBE6FF",true) };
            ((TextBlock)live.Child).HorizontalAlignment=HorizontalAlignment.Center; ((TextBlock)live.Child).VerticalAlignment=VerticalAlignment.Center;
            DockPanel.SetDock(live,Dock.Right);header.Children.Add(live);header.Children.Add(label);panel.Children.Add(header);panel.Children.Add(Text);Child=panel;
        }
        public void Update(Caption c,double size,string color) {
            string title=(c.Source.Id=="legacy"?"OUTPUT":c.Source.Id.ToUpperInvariant())+"  ·  "+c.Source.Name;
            if(label.Text!=title){label.Text=title;label.ToolTip=title;}
            if(Text.Text!=c.Text)Text.Text=c.Text; // Literal transcript text; never markup.
            if(Text.FontSize!=size){Text.FontSize=size;Text.LineHeight=size*1.4;}
            live.Visibility=c.Final?Visibility.Hidden:Visibility.Visible; // Reserve the same header space.
            if(accent!=color){BorderBrush=Theme.Brush(color);accent=color;}
        }
    }
}
