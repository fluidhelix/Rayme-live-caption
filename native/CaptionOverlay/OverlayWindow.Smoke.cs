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
            UiCheck(views.Count==2&&ActualWidth>=MinWidth&&controls.IsLoaded,"Overlay and modern Controls construct with two attributed caption rows");
            var row=views["b:2:1"];double height=row.ActualHeight;
            UiCheck(row.IsLive&&row.Text.FontStyle==FontStyles.Normal&&row.Text.FontWeight==FontWeights.Normal,"Partial captions use normal typography with a LIVE badge");
            Capture(this,Path.Combine(output,"overlay-partial.png"));
            state.Apply(partial.Replace("\"final\":false","\"final\":true"));Render();UpdateLayout();
            UiCheck(ReferenceEquals(row,views["b:2:1"])&&!row.IsLive&&views.Count==2,"Finalization reuses the same visual row without duplicates");
            UiCheck(row.Text.FontStyle==FontStyles.Normal&&row.Text.FontWeight==FontWeights.Normal&&Math.Abs(row.ActualHeight-height)<.1,"Partial-to-final transition preserves font and measured row height");
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
            UiCheck(!throughSetting!.IsEnabled&&!clickThrough,"Click-through stays disabled when the smoke harness has no registered recovery icon");
            var toggleTrack=(Border)topSetting.Template.FindName("Track",topSetting);UiCheck(((SolidColorBrush)toggleTrack.Background).Color==((SolidColorBrush)Theme.Brush("#8DD6FF")).Color,"Switch checked state has the intended visual treatment");
            var combo=selectors["a"].Select;combo.ApplyTemplate();UiCheck(combo.Template.FindName("PART_Popup",combo) is Popup,"Source selector uses the shared dropdown template");
            UiCheck(combo.FocusVisualStyle!=null&&topSetting.FocusVisualStyle!=null&&fontControl.FocusVisualStyle!=null,"Interactive controls have keyboard focus visuals");
            UiCheck(new IconBitmapDecoder(Theme.IconUri,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames.Count>=6,"Embedded Rayme icon includes multiple DPI resolutions");
            Capture(controls,Path.Combine(output,"controls-audio.png"));
            combo.IsDropDownOpen=true;await Task.Delay(100);var popup=(Popup)combo.Template.FindName("PART_Popup",combo);
            UiCheck(popup.IsOpen&&popup.Child is Border,"Styled application dropdown opens with real choice items");Capture((FrameworkElement)popup.Child,Path.Combine(output,"controls-dropdown.png"));
            var dismiss=new KeyEventArgs(Keyboard.PrimaryDevice,PresentationSource.FromVisual(controls),0,Key.Escape){RoutedEvent=PreviewKeyDownEvent};controls.RaiseEvent(dismiss);
            UiCheck(!combo.IsDropDownOpen&&controls.IsLoaded,"Esc dismisses the dropdown before closing Controls");
            var settingsScroll=Descendants<ScrollViewer>(controls).First();
            Descendants<TextBlock>(controls).First(t=>t.Text=="Caption appearance").BringIntoView();await Task.Delay(100);Capture(controls,Path.Combine(output,"controls-appearance.png"));
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
            Render();UiCheck(views.Count==2&&views.Values.All(v=>v.Visibility==Visibility.Visible),"Finalized captions remain visible after opening and closing controls");
            OpenControls();Click(Descendants<Button>(controls!).First(b=>b.Name=="ClearHistory"));
            using(var command=JsonDocument.Parse(smokeCommands.Last()))UiCheck(command.RootElement.GetProperty("type").GetString()=="clear","Clear history uses the existing shared command");
            state.Apply("""{"type":"cleared"}""");Render();UiCheck(views.Count==0,"Server clear acknowledgement removes every caption row");
            SmokePassed=true;
        } catch(Exception error) { SmokePassed=false;failure=error.ToString(); }
        finally {
            menu?.Close();
            try{Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"checks.json"),JsonSerializer.Serialize(new{passed=SmokePassed,checks=uiChecks,failure},new JsonSerializerOptions{WriteIndented=true}));}catch(IOException){}
            Close();
        }
    }
}
