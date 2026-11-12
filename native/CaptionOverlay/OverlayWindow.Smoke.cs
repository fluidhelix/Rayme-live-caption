using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace Rayme.Overlay;

internal sealed partial class OverlayWindow {
    readonly List<string> smokeCommands=[];
    readonly List<string> uiChecks=[];
    static IEnumerable<T> Descendants<T>(DependencyObject root) where T:DependencyObject {
        for(int i=0;i<VisualTreeHelper.GetChildrenCount(root);i++) {
            var child=VisualTreeHelper.GetChild(root,i);if(child is T t)yield return t;
            foreach(var nested in Descendants<T>(child))yield return nested;
        }
    }
    void UiCheck(bool ok,string message){if(!ok)throw new InvalidOperationException(message);uiChecks.Add(message);}
    static void Click(Button button)=>button.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
    static void Capture(FrameworkElement visual,string file) {
        visual.UpdateLayout();int w=(int)Math.Ceiling(visual.ActualWidth),h=(int)Math.Ceiling(visual.ActualHeight);
        var bitmap=new RenderTargetBitmap(w,h,96,96,PixelFormats.Pbgra32);bitmap.Render(visual);
        var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));using var output=File.Create(file);encoder.Save(output);
    }
    async Task SmokeAsync() {
        var output=Environment.GetEnvironmentVariable("RAYME_UI_SMOKE_OUTPUT")??Path.Combine(Environment.CurrentDirectory,"artifacts","overlay-ui");
        string? failure=null;TrayFlyout? menu=null;
        try {
            Directory.CreateDirectory(output);
            prefs=prefs with{ColorDevice="#A1D8C3"}; // A saved pre-redesign custom accent, outside the standard palette.
            state.Apply("""{"type":"snapshot","status":"Ready. Choose your conversation sources.","processSupported":true,"history":[],"sources":{},"devices":[],"apps":[{"id":"test-a","name":"Browser conversation","pid":100},{"id":"test-b","name":"Voice chat","pid":200},{"id":"test-alt","name":"Another application","pid":300}],"partials":{}}""");
            state.Apply("""{"type":"caption","source":{"id":"a","generation":1,"name":"Browser conversation","identity":"test-a"},"id":1,"final":true,"text":"[laughter] Every conversation deserves a place on your screen.","capturedAt":1}""");
            const string partial="""{"type":"caption","source":{"id":"b","generation":2,"name":"Voice chat","identity":"test-b"},"id":1,"final":false,"text":"*sighing* I’m here. Take your time — I can follow along.","capturedAt":2}""";
            state.Apply(partial);connectionText="Connected";Render();OpenControls();await Task.Delay(180);UpdateLayout();controls!.UpdateLayout();
            UiCheck(captions.Children.Count==2&&ActualWidth>=MinWidth&&controls.IsLoaded,"Overlay and modern Controls construct with two fixed attributed transcript panels");
            var row=panels["b"];double height=row.ActualHeight;
            UiCheck(row.IsLive&&row.Text.FontStyle==FontStyles.Normal&&row.Text.FontWeight==FontWeights.Normal,"Partial captions use normal typography with a LIVE badge");
            Capture(this,Path.Combine(output,"overlay-partial.png"));
            state.Apply(partial.Replace("\"final\":false","\"final\":true"));Render();UpdateLayout();
            UiCheck(ReferenceEquals(row,panels["b"])&&!row.IsLive&&captions.Children.Count==2,"Finalization reuses the same transcript panel without duplicates");
            UiCheck(row.Text.FontStyle==FontStyles.Normal&&row.Text.FontWeight==FontWeights.Normal&&Math.Abs(row.ActualHeight-height)<.1&&row.PartialText.Length==0&&row.FinalText.Contains("*sighing*"),"Partial-to-final transition preserves font and measured row height");
            UiCheck(row.SourceLabel.Contains("Voice chat")&&row.Text.Text.Contains("*sighing*"),"Finalization preserves source label and annotation text");
            Capture(this,Path.Combine(output,"overlay-final.png"));
            UiCheck(lockSetting!.Template.FindName("Track",lockSetting) is Border,"Window toggles use the shared switch template");
            UiCheck(colorControls["device"].Any(c=>(string)c.Tag=="#A1D8C3"&&c.IsChecked==true),"Existing custom saved colors remain represented and selected");
            UiCheck(fontControl!.Template.FindName("PART_Track",fontControl) is Track track&&track.Thumb.Width==18,"Appearance slider uses the shared track and thumb");
            fontControl.Value=34;opacityControl!.Value=.82;
            UiCheck(prefs.FontSize==34&&row.Text.FontSize==34&&Math.Abs(prefs.BackgroundOpacity-.82)<.001,"Appearance slider events update validated preferences and actual caption text");
            UiCheck(((SolidColorBrush)surface.Background).Color.A==(byte)(.82*255)&&Opacity==1,"Background opacity leaves transcript text opaque");
            colorControls["a"][1].IsChecked=true;UiCheck(prefs.ColorA=="#FFD68A","Accent swatch selection updates saved color preferences");
            topSetting!.IsChecked=false;UiCheck(!prefs.Topmost&&!Topmost,"Topmost switch changes the actual overlay behavior");topSetting.IsChecked=true;
            lockSetting.IsChecked=true;UiCheck(prefs.Locked&&ResizeMode==ResizeMode.NoResize,"Position switch locks actual resize behavior");lockSetting.IsChecked=false;
            for(int turn=0;turn<4;turn++){
                lockSetting.IsChecked=turn%2==0;UpdateLayout();
                var point=settingsButton.PointToScreen(new Point(settingsButton.ActualWidth/2,settingsButton.ActualHeight/2));
                UiCheck(DesktopPlacement.ClientHit(handle,point),"Settings remains a client-area hit target after lock toggle "+turn);
                UiCheck((string)lockButton.Content==WindowInteraction.LockGlyph(prefs.Locked)&&System.Windows.Automation.AutomationProperties.GetName(lockButton)==WindowInteraction.LockAction(prefs.Locked),"Lock icon and accessibility action reflect the actual state "+turn);
                controls!.Close();Click(settingsButton);await Task.Delay(60);UiCheck(controls?.IsLoaded==true,"Settings reopens Controls while position is locked or unlocked "+turn);
            }
            lockSetting!.IsChecked=false;
            UiCheck(!throughSetting!.IsEnabled&&!clickThrough,"Click-through stays disabled when the smoke harness has no registered recovery icon");
            var toggleTrack=(Border)topSetting.Template.FindName("Track",topSetting);UiCheck(((SolidColorBrush)toggleTrack.Background).Color==((SolidColorBrush)Theme.Brush("#8DD6FF")).Color,"Switch checked state has the intended visual treatment");
            var combo=selectors["a"].Select;combo.ApplyTemplate();UiCheck(combo.Template.FindName("PART_Popup",combo) is Popup,"Source selector uses the shared dropdown template");
            UiCheck(combo.FocusVisualStyle!=null&&topSetting.FocusVisualStyle!=null&&fontControl.FocusVisualStyle!=null,"Interactive controls have keyboard focus visuals");
            UiCheck(new IconBitmapDecoder(Theme.IconUri,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames.Count>=6,"Embedded Rayme icon includes multiple DPI resolutions");
            Capture(controls!,Path.Combine(output,"controls-audio.png"));
            combo.IsDropDownOpen=true;await Task.Delay(100);var popup=(Popup)combo.Template.FindName("PART_Popup",combo);
            UiCheck(popup.IsOpen&&popup.Child is Border,"Styled application dropdown opens with real choice items");Capture((FrameworkElement)popup.Child,Path.Combine(output,"controls-dropdown.png"));
            var dismiss=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(controls),0,Key.Escape){RoutedEvent=PreviewKeyDownEvent};controls!.RaiseEvent(dismiss);
            UiCheck(!combo.IsDropDownOpen&&controls.IsLoaded,"Esc dismisses the dropdown before closing Controls");
            var settingsScroll=Descendants<ScrollViewer>(controls).First();
            Descendants<TextBlock>(controls).First(t=>t.Text=="Captions").BringIntoView();await Task.Delay(100);Capture(controls,Path.Combine(output,"controls-appearance.png"));
            settingsScroll.ScrollToEnd();await Task.Delay(100);Capture(controls,Path.Combine(output,"controls-session.png"));
            combo.SelectedValue="test-alt";
            using(var command=JsonDocument.Parse(smokeCommands.Last()))UiCheck(command.RootElement.GetProperty("type").GetString()=="configure"&&command.RootElement.GetProperty("slot").GetString()=="a"&&command.RootElement.GetProperty("id").GetString()=="test-alt","Application selection sends the existing configure command");
            selectors["b"].Enabled.IsChecked=false;
            using(var command=JsonDocument.Parse(smokeCommands.Last()))UiCheck(!command.RootElement.GetProperty("enabled").GetBoolean(),"Application switch sends a disable command");
            Click(pauseButton!);using(var command=JsonDocument.Parse(smokeCommands.Last()))UiCheck(command.RootElement.GetProperty("paused").GetBoolean(),"Pause button sends the existing pause command");
            state.Apply("""{"type":"paused","paused":true}""");UpdateSettings();Click(pauseButton!);
            using(var command=JsonDocument.Parse(smokeCommands.Last()))UiCheck(!command.RootElement.GetProperty("paused").GetBoolean(),"Server pause acknowledgement changes button to resume");
            var buttons=Descendants<Button>(controls).ToArray();Click(buttons.First(b=>b.Name=="RefreshAvailability"));
            using(var command=JsonDocument.Parse(smokeCommands.Last()))UiCheck(command.RootElement.GetProperty("type").GetString()=="refresh","Refresh button uses existing server command");
            controls.Width=400;controls.Height=560;await Task.Delay(100);Capture(controls,Path.Combine(output,"controls-compact.png"));UiCheck(controls.ActualWidth>=400&&settingsScroll.ScrollableHeight>0,"Compact Controls remain scrollable at minimum width");
            controls.Close();
            menu=new TrayFlyout(BuildTrayActions());var display=DesktopPlacement.Displays().First();menu.ShowAt(new(display.Work.X+display.Work.Width-40,display.Work.Y+display.Work.Height,24,24));await Task.Delay(100);
            UiCheck(menu.ActionCount==6&&menu.FirstAction.FocusVisualStyle!=null,"Tray flyout contains all six recovery actions and focus styling");Capture(menu,Path.Combine(output,"tray-flyout.png"));
            var end=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(menu),0,Key.End){RoutedEvent=PreviewKeyDownEvent};menu.RaiseEvent(end);UiCheck(end.Handled&&menu.FocusedAction=="Quit Rayme","Tray End key focuses the last menu action");
            var down=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(menu),0,Key.Down){RoutedEvent=PreviewKeyDownEvent};menu.RaiseEvent(down);UiCheck(down.Handled&&menu.FocusedAction=="Show overlay","Tray arrow navigation wraps to the first action");
            menu.Close();menu=null;
            using(var fallback=TrayIcon.FallbackMenu(BuildTrayActions()))UiCheck(fallback.Items.Count==6&&fallback.Items[3].Text=="Disable click-through"&&fallback.Items[4].Text=="Restore to primary monitor","Native fallback retains every recovery action");
            Render();UiCheck(captions.Children.Count==2&&panels["b"].Visibility==Visibility.Visible&&panels["b"].FinalText.Contains("*sighing*"),"Finalized captions remain visible after opening and closing controls");
            OpenControls();
            HandleSetup("""{"type":"setup","required":true,"phase":"choose","processingMode":"auto","message":"Choose a model.","activeModel":null}""");
            UiCheck(!downloadModel!.IsEnabled&&!useModel!.IsEnabled,"Model availability never implies selection or download consent");
            chooseDownload!.IsChecked=true;UiCheck(downloadModel.IsEnabled&&!useModel!.IsEnabled,"Recommended-model choice enables only the explicit download action");
            chooseLocal!.IsChecked=true;modelPath!.Text=@"C:\model.bin";UiCheck(useModel!.IsEnabled&&!downloadModel!.IsEnabled,"Local-model choice enables only the selected local model action");
            HandleSetup("""{"type":"setup","required":true,"phase":"loading","processingMode":"cpu","message":"Loading model.","activeModel":null}""");
            UiCheck(!useModel!.IsEnabled&&!downloadModel!.IsEnabled&&activeModel!.Text.Contains("Not loaded"),"Loading cannot claim an active model or allow competing setup actions");
            HandleSetup("""{"type":"setup","required":false,"phase":"ready","processingMode":"cpu","message":"Ready.","activeModel":{"name":"ggml-large-v3-turbo.bin"}}""");
            UiCheck(activeModel!.Text.Contains("ggml-large-v3-turbo.bin")&&processingChoices.Count==1,"Ready names the active model and exposes one processing selector");
            // Source panels and scroll positions are independent of one another.
            for(int i=2;i<28;i++)state.Apply(JsonSerializer.Serialize(new{type="caption",source=new{id="a",generation=1,name="Browser conversation",identity="test-a"},id=i,final=true,text="Conversation line "+i+" with enough words to wrap across this narrow panel.",capturedAt=i}));
            Render();await Task.Delay(80);UpdateLayout();panels["a"].Scroll.ScrollToTop();await Task.Delay(80);
            double reread=panels["a"].Scroll.VerticalOffset;var fixedA=panels["a"];var fixedB=panels["b"];
            state.Apply("""{"type":"caption","source":{"id":"b","generation":2,"name":"Voice chat","identity":"test-b"},"id":2,"final":false,"text":"More live words","capturedAt":100}""");Render();await Task.Delay(80);
            UiCheck(ReferenceEquals(fixedA,panels["a"])&&ReferenceEquals(fixedB,panels["b"])&&panels["a"].Scroll.VerticalOffset==reread,"Other-source captions preserve panel identity and the reader's independent scroll position");
            state.Apply(JsonSerializer.Serialize(new{type="caption",source=new{id="b",generation=2,name="Voice chat",identity="test-b"},id=2,revision=2,final=false,text=string.Join(" ",Enumerable.Repeat("More conversation",80)),capturedAt=100}));
            state.Apply(JsonSerializer.Serialize(new{type="caption",source=new{id="a",generation=1,name="Browser conversation",identity="test-a"},id=28,final=true,text="Speech added while reading earlier words",capturedAt=101}));Render();await Task.Delay(100);
            UiCheck(panels["a"].Scroll.VerticalOffset==reread&&Math.Abs(panels["b"].Scroll.VerticalOffset-panels["b"].Scroll.ScrollableHeight)<1,"New text respects rereading in A while independently following B");
            state.Apply("""{"type":"audio-level","source":{"id":"a","generation":1,"name":"Browser conversation","identity":"test-a"},"rms":20,"dbfs":-64,"low":true}""");UpdateSettings();UiCheck(sourceStatus!.Text.Contains("increase application playback volume"),"Low-signal feedback explains playback volume without exposing audio");
            state.Apply("""{"type":"paused","paused":true}""");Render();UiCheck(captions.Children.Count==2&&panels["a"].FinalText.Contains("Conversation line"),"Pausing preserves fixed panel layout and continuous finalized conversation");
            Click(Descendants<Button>(controls!).First(b=>b.Name=="ClearHistory"));
            using(var command=JsonDocument.Parse(smokeCommands.Last()))UiCheck(command.RootElement.GetProperty("type").GetString()=="clear","Clear history uses the existing shared command");
            state.Apply("""{"type":"cleared"}""");Render();UiCheck(panels.Values.All(p=>!p.IsLive)&&state.History.Count==0,"Server clear acknowledgement removes every caption row");
            state.Apply("""{"type":"source-removed","source":{"id":"a","generation":1,"name":"Browser conversation","identity":"test-a"}}""");
            state.Apply("""{"type":"source-removed","source":{"id":"b","generation":2,"name":"Voice chat","identity":"test-b"}}""");
            state.Apply("""{"type":"reset","source":{"id":"device","generation":3,"name":"Windows playback","identity":"default"}}""");Render();
            UiCheck(captions.Children.Count==1&&ReferenceEquals(captions.Children[0],panels["device"]),"Whole-device mode uses a single full-height transcript panel");
            SmokePassed=true;
        } catch(Exception error) { SmokePassed=false;failure=error.ToString(); }
        finally {
            menu?.Close();
            try{Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"checks.json"),JsonSerializer.Serialize(new{passed=SmokePassed,checks=uiChecks,failure},new JsonSerializerOptions{WriteIndented=true}));}catch(IOException){}
            Close();
        }
    }
}
