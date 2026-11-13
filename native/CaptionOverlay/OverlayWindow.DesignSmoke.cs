using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace Rayme.Overlay;
internal sealed partial class OverlayWindow {
    async Task ApprovedDesignSmoke(string output){
        UiCheck(((SolidColorBrush)surface.Background).Color is {R:0,G:0,B:0},"Overlay paints pure black with the existing background alpha");
        UiCheck(fontControl!.Minimum==16&&fontControl.Maximum==60&&fontControl.Value==prefs.FontSize,"Text-size slider retains 16–60 range and its live preference");
        UiCheck(panels["a"].SourceLabel==state.Sources["a"].Source.Name&&panels["a"].BorderThickness.Left==0,"Source title uses the actual name and no colored left bar");
        int commands=smokeCommands.Count;
        Detach();await Task.Delay(100);UpdateLayout();
        UiCheck(layout.Detached&&detached.Count==2&&!IsVisible&&detached.Values.All(w=>w.IsVisible),"Explicit detach shows two peer caption windows and hides only the combined host");
        UiCheck(smokeCommands.Count==commands,"Detaching sends no capture, model or backend command");
        UiCheck(detached["a"].Body.Children.Contains(panels["a"])&&detached["b"].Body.Children.Contains(panels["b"]),"Detached views reuse the same continuous transcript and scroll objects");
        ToggleSourceLock("a");UiCheck(detached["a"].ResizeMode==ResizeMode.NoResize&&detached["b"].ResizeMode!=ResizeMode.NoResize,"Detached locks affect only their own window");
        OpenSourceControls(detached["a"]);await Task.Delay(40);UiCheck(controls!.IsVisible&&controls.Owner==detached["a"],"Settings remains accessible and correctly owned from a locked detached window");
        Capture(detached["a"],Path.Combine(output,"detached-a.png"));Capture(detached["b"],Path.Combine(output,"detached-b.png"));
        detached["a"].Width=520;await Task.Delay(50);UiCheck(detached["b"].Width!=520,"Detached window dimensions are independent");
        Reattach();await Task.Delay(80);UiCheck(!layout.Detached&&detached.Count==0&&IsVisible&&captions.Children.Count==2,"Reattach restores the existing combined host and both panels");
        // A temporary source removal must not destroy the main smoke history.
        var snapshot=state.Sources["b"];state.Sources.Remove("b");Render();UiCheck(captions.Children.Count==1&&!toolbar.Layout.IsEnabled,"A alone expands and Detach is disabled");state.Sources["b"]=snapshot;Render();
        Detach();state.Sources.Remove("a");Render();await Task.Delay(40);UiCheck(!layout.Detached&&captions.Children.Count==1&&captions.Children[0]==panels["b"],"Disabling one detached source returns the other to a full combined panel");
        // Restore the synthetic source identity without touching backend protocol state.
        state.Apply("""{"type":"source-status","source":{"id":"a","generation":1,"name":"Browser conversation","identity":"test-a"},"state":"waiting","message":"Listening"}""");Render();UiCheck(!layout.Detached&&captions.Children.Count==2,"Enabling the second source never auto-detaches");
        var picker=new ColorPicker("Browser conversation",prefs.ColorA,SourceColor.Default("a")){Owner=this};picker.Show();await Task.Delay(50);
        UiCheck(((SolidColorBrush)((Border)picker.Content).Background).Color.R==((SolidColorBrush)((Border)picker.Content).Background).Color.B,"Color picker uses neutral charcoal");
        picker.HexField.Text="#A3D9B1";UiCheck(picker.Selected=="#A3D9B1"&&picker.ApplyButton.IsEnabled,"Native HEX editing updates the selected color");
        picker.HexField.Text="invalid";UiCheck(!picker.ApplyButton.IsEnabled,"Invalid native color cannot be applied");Click(picker.RestoreButton);UiCheck(picker.Selected=="#80DFFF","Native Restore default uses the original A color");Capture(picker,Path.Combine(output,"color-picker.png"));picker.Close();
        var savedB=prefs.ColorB;
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(()=>{var dialog=Application.Current.Windows.OfType<ColorPicker>().Single();dialog.HexField.Text="#A3D9B1";Click(dialog.ApplyButton);}));
        OpenColorPicker("a");UiCheck(prefs.ColorA=="#A3D9B1"&&prefs.ColorB==savedB,"Applying the native picker changes exactly one persisted source preference");
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(()=>{var dialog=Application.Current.Windows.OfType<ColorPicker>().Single();dialog.HexField.Text="#000000";Click(Descendants<Button>(dialog).First(b=>b.ToolTip as string=="Cancel"));}));
        OpenColorPicker("a");UiCheck(prefs.ColorA=="#A3D9B1","Cancel leaves the saved source color unchanged");
        _ = Dispatcher.BeginInvoke(System.Windows.Threading.DispatcherPriority.Loaded,new Action(()=>{var dialog=Application.Current.Windows.OfType<Window>().Single(w=>w.Title=="Rayme · Theme probe");var background=((SolidColorBrush)((Border)dialog.Content).Background).Color;UiCheck(background.R==background.G&&background.G==background.B,"Rayme-owned error dialogs use neutral charcoal");Click(Descendants<Button>(dialog).First(b=>(string)b.Content=="OK"));}));
        UiCheck(Rayme.CharcoalDialog.Show("Synthetic dialog fixture.","Rayme · Theme probe",0x10)==1,"Charcoal startup/error prompt preserves OK behavior");
        await CaptionStabilitySmoke(output);
    }
    async Task CaptionStabilitySmoke(string output){
        // Reproduce the legacy mechanism with the old auto-scroll policy. This
        // hermetic probe records geometry only; all content is synthetic.
        var legacyText=new TextBlock{TextWrapping=TextWrapping.Wrap,FontSize=22};var legacyScroll=new ScrollViewer{Content=legacyText,VerticalScrollBarVisibility=ScrollBarVisibility.Auto};
        var probe=new Window{Width=430,Height=260,Content=legacyScroll,Owner=this,ShowInTaskbar=false};probe.Show();
        legacyText.Text=string.Join(" ",Enumerable.Repeat("synthetic legacy words",100));await Task.Delay(60);legacyScroll.ScrollToEnd();await Task.Delay(60);
        var legacyBefore=new{offset=legacyScroll.VerticalOffset,extent=legacyScroll.ExtentHeight,viewport=legacyScroll.ViewportHeight,width=legacyScroll.ViewportWidth};
        legacyText.Text="Corrected synthetic final.";await Task.Delay(60);legacyScroll.ScrollToEnd();await Task.Delay(60);
        var legacyAfter=new{offset=legacyScroll.VerticalOffset,extent=legacyScroll.ExtentHeight,viewport=legacyScroll.ViewportHeight,width=legacyScroll.ViewportWidth};
        UiCheck(legacyBefore.offset>legacyAfter.offset+1,"Legacy final correction reproduces bottom-follow/extent-clamp movement on native WPF");probe.Close();
        var source=new Source("a",1,"Synthetic conversation","synthetic");var sample=new CaptionState();
        void Event(long id,bool final,string text,long revision=1)=>sample.Apply(JsonSerializer.Serialize(new{type="caption",source=new{id=source.Id,generation=source.Generation,name=source.Name,identity=source.Identity},id,revision,final,text,capturedAt=id}));
        for(int i=1;i<=12;i++)Event(i,true,"Readable earlier conversation remains available while the current sentence is corrected.");
        const string provisional="These are the words I can already read while you finish the thought. More provisional words may be corrected at the end.";
        Event(13,false,provisional);
        var panel=new TranscriptPanel("a",true);var window=new Window{Width=450,Height=300,Content=panel,Owner=this,ShowInTaskbar=false};window.Show();panel.Update(sample,22,"#80DFFF",false);await Task.Delay(100);
        panel.Scroll.ScrollToVerticalOffset(Math.Max(0,panel.Scroll.ScrollableHeight-20));await Task.Delay(70);
        var mark=panel.ReadingMark()??throw new InvalidOperationException("Native reading anchor unavailable");double width=panel.Text.ViewportWidth,height=panel.Text.ViewportHeight;
        UiCheck(mark.Key!="a:1:13","Correction probe anchors a surviving finalized word");
        Event(13,true,"These are the words I can already read.",2);panel.Update(sample,22,"#80DFFF",false);await Task.Delay(100);
        var finalY=panel.MarkY(mark.Key,mark.Index);
        UiCheck(finalY.HasValue&&Math.Abs(finalY.Value-mark.Y)<1,"Partial-to-final shrink preserves the native reading word's screen position");
        UiCheck(panel.Text.ViewportWidth==width&&panel.Text.ViewportHeight==height,"Finalization and LIVE keep native wrapping width and viewport geometry stable");
        var stableOffset=panel.Text.VerticalOffset;panel.Update(sample,22,"#80DFFF",false);await Task.Delay(60);UiCheck(Math.Abs(panel.Text.VerticalOffset-stableOffset)<1,"Silence/status-only update does not scroll the native transcript");
        panel.Text.SelectAll();UiCheck(!panel.Text.Selection.IsEmpty,"Native caption text supports normal selection and copy");
        File.WriteAllText(Path.Combine(output,"caption-geometry.json"),JsonSerializer.Serialize(new{legacyBefore,legacyAfter,current=panel.Geometry()},new JsonSerializerOptions{WriteIndented=true}));
        window.Close();
    }
}
