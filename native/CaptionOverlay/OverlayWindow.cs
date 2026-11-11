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
using Forms = System.Windows.Forms;

namespace Rayme.Overlay;

internal sealed class OverlayWindow : Window {
    readonly SessionClient? client;
    readonly bool smoke;
    readonly CaptionState state = new();
    readonly string settingsFile = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Rayme", "CaptionOverlay", "settings.json");
    Preferences prefs;
    readonly Border surface = new() { CornerRadius = new CornerRadius(14), BorderThickness = new Thickness(1), BorderBrush = Brush("#647084"), Padding = new Thickness(12,6,12,10) };
    readonly StackPanel captions = new();
    readonly ScrollViewer scroll = new() { VerticalScrollBarVisibility = ScrollBarVisibility.Auto, HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled };
    readonly TextBlock connection = new() { FontSize = 12, Foreground = Brush("#A8E5D0"), Margin = new Thickness(0,2,0,4), TextWrapping = TextWrapping.Wrap };
    readonly TextBlock waiting = new() { Text = "Waiting for conversation…\nOpen Controls to enable applications.", Foreground = Brush("#E0E7F2"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(4,12,4,4) };
    readonly Dictionary<string, CaptionView> views = [];
    readonly Button lockButton;
    readonly DispatcherTimer saveTimer = new() { Interval = TimeSpan.FromMilliseconds(500) };
    readonly DispatcherTimer displayTimer = new() { Interval = TimeSpan.FromMilliseconds(300) };
    Forms.NotifyIcon? tray;
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
    static Button Action(string text, RoutedEventHandler handler) {
        var button = new Button { Content = text, Padding = new Thickness(9,5,9,5), Margin = new Thickness(3), MinHeight = 30, Background = Brush("#29364C"), Foreground = Brushes.White, BorderBrush = Brush("#718198"), Cursor = Cursors.Hand };
        button.Click += handler; return button;
    }
    public OverlayWindow(SessionClient? session, bool isSmoke = false) {
        client = session; smoke = isSmoke; prefs = smoke ? new() : Preferences.Load(settingsFile);
        Title = "Rayme Smart Live Captions"; Width = 640; Height = 360; MinWidth = 300; MinHeight = 180;
        WindowStyle = WindowStyle.None; ResizeMode = ResizeMode.CanResizeWithGrip; AllowsTransparency = true;
        Background = Brushes.Transparent; Foreground = Brushes.White; FontFamily = new FontFamily("Segoe UI");
        ShowInTaskbar = true; ShowActivated = false; Topmost = prefs.Topmost;
        WindowChrome.SetWindowChrome(this, new WindowChrome { CaptionHeight = 0, ResizeBorderThickness = new Thickness(7), CornerRadius = new CornerRadius(14), GlassFrameThickness = new Thickness(0), UseAeroCaptionButtons = false });
        var grid = new Grid(); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new() { Height = GridLength.Auto }); grid.RowDefinitions.Add(new());
        var header = new DockPanel { LastChildFill = true };
        var hide = Action("Hide", (_,_) => { if (tray != null) Hide(); }); hide.ToolTip = "Hide to notification area; use the Rayme tray icon to restore"; DockPanel.SetDock(hide,Dock.Right); header.Children.Add(hide);
        var settings = Action("Controls", (_,_) => OpenControls()); DockPanel.SetDock(settings,Dock.Right); header.Children.Add(settings);
        lockButton = Action("", (_,_) => Change(prefs with { Locked = !prefs.Locked })); DockPanel.SetDock(lockButton,Dock.Right); header.Children.Add(lockButton);
        var drag = new TextBlock { Text = "⠿  RAYME", FontSize = 12, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, Padding = new Thickness(4,8,2,8), Foreground = Brush("#CAD5E7"), Cursor = Cursors.SizeAll, ToolTip = "Drag to any monitor (unlock first)" };
        drag.MouseLeftButtonDown += (_,e) => { if (!prefs.Locked && !clickThrough && e.ButtonState == MouseButtonState.Pressed) { try { DragMove(); } catch (InvalidOperationException) { } } }; header.Children.Add(drag);
        grid.Children.Add(header); Grid.SetRow(connection,1); grid.Children.Add(connection);
        scroll.Content = captions; Grid.SetRow(scroll,2); grid.Children.Add(scroll); surface.Child = grid; Content = surface;
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
        lockButton.Content = prefs.Locked ? "Unlock" : "Lock";
        lockButton.ToolTip = prefs.Locked ? "Position and size locked" : "Lock position and size";
    }
    void Through(bool enabled) {
        // Only enable once a live tray recovery surface exists; never persist this mode.
        if (enabled && tray == null) return;
        clickThrough = enabled; DesktopPlacement.ClickThrough(handle, enabled); UpdateConnection(); UpdateSettings();
    }
    void Reveal(bool settings = false) {
        Through(false); if (!IsVisible) Show(); if (WindowState != WindowState.Normal) WindowState = WindowState.Normal;
        Recover(false); Activate(); if (settings) OpenControls();
    }
    void CreateTray() {
        try {
            tray = new Forms.NotifyIcon { Icon = System.Drawing.SystemIcons.Information, Text = "Rayme captions — double-click to recover", Visible = true };
            var menu = new Forms.ContextMenuStrip();
            menu.Items.Add("Show / disable click-through", null, (_,_) => Dispatcher.Invoke(() => Reveal()));
            menu.Items.Add("Controls", null, (_,_) => Dispatcher.Invoke(() => Reveal(true)));
            menu.Items.Add("Unlock and restore to primary monitor", null, (_,_) => Dispatcher.Invoke(() => { Change(prefs with { Locked = false }); Reveal(); Recover(true); }));
            menu.Items.Add("Exit overlay (browser fallback)", null, (_,_) => Dispatcher.Invoke(Close));
            tray.ContextMenuStrip = menu; tray.DoubleClick += (_,_) => Dispatcher.Invoke(() => Reveal());
        } catch { tray?.Dispose(); tray = null; connectionText = "Tray unavailable; keep this window visible. Click-through disabled."; }
    }
    async Task RunClient() {
        try { await client!.RunAsync(json => Dispatcher.InvokeAsync(() => { state.Apply(json); Render(); UpdateSettings(); }).Task,
            status => Dispatcher.InvokeAsync(() => { connectionText = status; UpdateConnection(); }).Task); }
        catch { if (!closing) { connectionText = "Caption connection failed. Restart Windows caption mode."; UpdateConnection(); } }
    }
    void UpdateConnection() { connection.Text = connectionText + (state.Paused ? " · Capture paused" : "") + (clickThrough ? " · Click-through: use tray to recover" : ""); }
    void Render() {
        UpdateConnection();
        bool follow = scroll.ScrollableHeight - scroll.VerticalOffset < 60;
        var desired = state.History.Concat(state.Partials.OrderBy(x => x.Key).Select(x => x.Value)).Where(x => x.Text.Length > 0).ToArray();
        var keys = desired.Select(x => x.Key).ToHashSet();
        foreach (string key in views.Keys.Where(x => !keys.Contains(x)).ToArray()) { captions.Children.Remove(views[key]); views.Remove(key); }
        captions.Children.Remove(waiting);
        for (int i = 0; i < desired.Length; i++) {
            var c = desired[i];
            if (!views.TryGetValue(c.Key, out var view)) { view = new CaptionView(); views[c.Key] = view; }
            string color = c.Source.Id == "a" ? prefs.ColorA : c.Source.Id == "b" ? prefs.ColorB : prefs.ColorDevice;
            view.Update(c,prefs.FontSize,color);
            if (captions.Children.IndexOf(view) != i) { captions.Children.Remove(view); captions.Children.Insert(i,view); }
        }
        if (desired.Length == 0) captions.Children.Add(waiting);
        if (follow) scroll.ScrollToEnd();
    }
    async Task Send(object command) {
        if (client == null || !await client.SendAsync(command)) { connectionText = "Control not sent — connection unavailable or busy. Please retry."; UpdateConnection(); }
    }
    static TextBlock Heading(string text) => new() { Text = text, FontSize = 17, FontWeight = FontWeights.SemiBold, Margin = new Thickness(0,16,0,6), Foreground = Brush("#9DDFFF") };
    void OpenControls() {
        Through(false);
        if (controls != null) { controls.Show(); controls.Activate(); return; }
        var panel = new StackPanel { Margin = new Thickness(18) };
        controls = new Window { Title = "Rayme · Controls & appearance", Width = 450, Height = 700, MinWidth = 360, MinHeight = 360, Owner = this,
            Background = Brush("#111B2B"), Foreground = Brushes.White, FontFamily = FontFamily, FontSize = 14, Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto }, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        controls.Closed += (_,_) => { controls = null; selectors.Clear(); };
        backendStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush("#D5DFEC") }; panel.Children.Add(backendStatus);
        panel.Children.Add(Heading("Conversation sources"));
        panel.Children.Add(new TextBlock { Text = "Enable up to two applications OR whole-device fallback. Changes apply to every connected display.", TextWrapping = TextWrapping.Wrap });
        foreach (string slot in new[] { "a", "b", "device" }) {
            var enabled = new CheckBox { Content = slot == "device" ? "Whole-device fallback" : "Application " + slot.ToUpperInvariant(), Foreground = Brushes.White, Margin = new Thickness(0,10,0,5) };
            var select = new ComboBox { MinHeight = 32, DisplayMemberPath = "Name", SelectedValuePath = "Id", Foreground = Brush("#111B2B"), Background = Brush("#F1F4FA"), ToolTip = "Choose a source; enable its checkbox to capture" };
            selectors[slot] = (select, enabled); panel.Children.Add(enabled); panel.Children.Add(select);
            async void Configure(object? sender, RoutedEventArgs e) {
                if (syncing) return;
                string? id = (select.SelectedItem as Choice)?.Id;
                if (enabled.IsChecked == true && id == null) { syncing = true; enabled.IsChecked = false; syncing = false; return; }
                await Send(new { type = "configure", slot, id, enabled = enabled.IsChecked == true });
            }
            enabled.Checked += Configure; enabled.Unchecked += Configure;
            select.SelectionChanged += (sender,e) => { if (enabled.IsChecked == true) Configure(sender,e); };
        }
        var actions = new WrapPanel(); panel.Children.Add(actions);
        actions.Children.Add(Action("Refresh availability", async (_,_) => await Send(new { type = "refresh" })));
        pauseButton = Action("Pause capture", async (_,_) => await Send(new { type = "pause", paused = !state.Paused })); actions.Children.Add(pauseButton);
        actions.Children.Add(Action("Clear history everywhere", async (_,_) => await Send(new { type = "clear" })));
        sourceStatus = new TextBlock { TextWrapping = TextWrapping.Wrap, Foreground = Brush("#B8C6DB"), Margin = new Thickness(0,6,0,0) }; panel.Children.Add(sourceStatus);
        panel.Children.Add(Heading("Reading comfort"));
        AddSlider(panel,"Text size (independent of window)",16,60,prefs.FontSize,n => Change(prefs with { FontSize = n }));
        AddSlider(panel,"Background opacity (70–100%; text stays opaque)",.70,1,prefs.BackgroundOpacity,n => Change(prefs with { BackgroundOpacity = n }));
        foreach (var item in new[] { ("a","Application A accent"), ("b","Application B accent"), ("device","Playback accent") }) {
            panel.Children.Add(new TextBlock { Text = item.Item2, Margin = new Thickness(0,8,0,3) });
            var color = new ComboBox { ItemsSource = new[] { new Choice("#80DFFF","Sky blue"), new Choice("#FFD68A","Warm amber"), new Choice("#CBB9FF","Soft lilac"), new Choice("#9CE9BA","Mint green"), new Choice("#FFB8D2","Rose pink"), new Choice("#F1F4FA","Moon white") }, DisplayMemberPath = "Name", SelectedValuePath = "Id", MinHeight = 30, Foreground = Brush("#111B2B"), Background = Brush("#F1F4FA") };
            color.SelectedValue = item.Item1 == "a" ? prefs.ColorA : item.Item1 == "b" ? prefs.ColorB : prefs.ColorDevice;
            color.SelectionChanged += (_,_) => { if (color.SelectedItem is Choice choice) { string c = choice.Id; Change(item.Item1 == "a" ? prefs with { ColorA = c } : item.Item1 == "b" ? prefs with { ColorB = c } : prefs with { ColorDevice = c }); } }; panel.Children.Add(color);
        }
        panel.Children.Add(Heading("Window"));
        lockSetting = Setting(panel,"Lock position and size",prefs.Locked,v => Change(prefs with { Locked = v }));
        topSetting = Setting(panel,"Always on top",prefs.Topmost,v => Change(prefs with { Topmost = v }));
        throughSetting = Setting(panel,"Click-through (recover using Rayme tray icon)",false,Through); throughSetting.IsEnabled = tray != null;
        panel.Children.Add(Action("Restore placement on primary monitor", (_,_) => { Through(false); Recover(true); }));
        panel.Children.Add(new TextBlock { Text = "Preferences save automatically. Audio, captions and session credentials are never saved. Closing this control panel leaves the overlay running.", TextWrapping = TextWrapping.Wrap, Foreground = Brush("#B8C6DB"), Margin = new Thickness(0,14,0,4) });
        UpdateSettings(); controls.Show();
    }
    CheckBox Setting(Panel panel, string label, bool initial, Action<bool> action) {
        var check = new CheckBox { Content = label, IsChecked = initial, Foreground = Brushes.White, Margin = new Thickness(0,5,0,5) };
        check.Checked += (_,_) => { if (!syncing) action(true); }; check.Unchecked += (_,_) => { if (!syncing) action(false); }; panel.Children.Add(check); return check;
    }
    static void AddSlider(Panel panel,string name,double min,double max,double value,Action<double> change) {
        var label = new TextBlock { Margin = new Thickness(0,8,0,4) }; panel.Children.Add(label);
        var slider = new Slider { Minimum = min, Maximum = max, Value = value, IsMoveToPointEnabled = true, SmallChange = max > 1 ? 1 : .05, LargeChange = max > 1 ? 4 : .1 };
        label.Text = $"{name}: {value:0.##}"; System.Windows.Automation.AutomationProperties.SetName(slider,name);
        slider.ValueChanged += (_,e) => { label.Text = $"{name}: {e.NewValue:0.##}"; change(e.NewValue); }; panel.Children.Add(slider);
    }
    void UpdateSettings() {
        if (controls == null) return;
        syncing = true;
        try {
            backendStatus!.Text = state.Status;
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
            sourceStatus!.Text = string.Join("\n",state.Sources.Values.Select(s => $"{s.Source.Id.ToUpperInvariant()} · {s.Source.Name}: {s.State}"));
            if (lockSetting != null) lockSetting.IsChecked = prefs.Locked;
            if (topSetting != null) topSetting.IsChecked = prefs.Topmost;
            if (throughSetting != null) throughSetting.IsChecked = clickThrough;
        } finally { syncing = false; }
    }
    async Task SmokeAsync() {
        try {
            state.Apply("""{"type":"snapshot","status":"Synthetic GUI smoke test","history":[],"sources":{},"devices":[],"apps":[],"partials":{}}""");
            state.Apply("""{"type":"caption","source":{"id":"a","generation":1,"name":"Application A","identity":"test-a"},"id":1,"final":true,"text":"[laughter] Safe <markup> & readable captions","capturedAt":1}""");
            state.Apply("""{"type":"caption","source":{"id":"b","generation":2,"name":"Application B","identity":"test-b"},"id":1,"final":false,"text":"*sighing* Independent partial caption","capturedAt":2}""");
            connectionText = "Synthetic captions · no audio or Whisper"; Render(); OpenControls();
            await Task.Delay(700); UpdateLayout();
            SmokePassed = views.Count == 2 && ActualWidth >= MinWidth && ActualHeight >= MinHeight && controls?.IsLoaded == true;
        } catch { SmokePassed = false; }
        finally { Close(); }
    }
    sealed class CaptionView : Border {
        readonly TextBlock label = new() { FontSize = 13, FontWeight = FontWeights.SemiBold, Foreground = Brush("#E0E7F2"), TextTrimming = TextTrimming.CharacterEllipsis };
        readonly TextBlock text = new() { TextWrapping = TextWrapping.Wrap, Foreground = Brush("#F8FAFC"), Margin = new Thickness(0,3,0,0) };
        public CaptionView() { BorderThickness = new Thickness(3,0,0,0); Padding = new Thickness(10,4,4,5); Margin = new Thickness(0,5,0,5); var panel = new StackPanel(); panel.Children.Add(label); panel.Children.Add(text); Child = panel; }
        public void Update(Caption c, double size, string color) {
            string title = (c.Source.Id == "legacy" ? "OUTPUT" : c.Source.Id.ToUpperInvariant()) + " · " + c.Source.Name + (c.Final ? "" : " · live");
            if (label.Text != title) { label.Text = title; label.ToolTip = title; }
            if (text.Text != c.Text) text.Text = c.Text; // Literal TextBlock.Text; never parse transcript markup.
            text.FontSize = size; text.FontStyle = c.Final ? FontStyles.Normal : FontStyles.Italic;
            BorderBrush = Brush(color); Background = c.Final ? Brushes.Transparent : Brush("#29364C");
        }
    }
}
