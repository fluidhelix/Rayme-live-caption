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
        var combinedMark=panels["a"].ReadingMark();bool wasFollowing=panels["a"].Following;
        Detach();await Task.Delay(100);UpdateLayout();
        UiCheck(layout.Detached&&detached.Count==2&&!IsVisible&&detached.Values.All(w=>w.IsVisible),"Explicit detach shows two peer caption windows and hides only the combined host");
        UiCheck(smokeCommands.Count==commands,"Detaching sends no capture, model or backend command");
        UiCheck(detached["a"].Body.Children.Contains(panels["a"])&&detached["b"].Body.Children.Contains(panels["b"]),"Detached views reuse the same continuous transcript and scroll objects");
        UiCheck(panels["a"].Following==wasFollowing&&(wasFollowing||combinedMark is {} cm&&Math.Abs((panels["a"].MarkY(cm.Key,cm.Index)??double.MaxValue)-cm.Y)<1),"Detaching preserves A's independent reading anchor and intent through reflow");
        ToggleSourceLock("a");UiCheck(detached["a"].ResizeMode==ResizeMode.NoResize&&detached["b"].ResizeMode!=ResizeMode.NoResize,"Detached locks affect only their own window");
        OpenSourceControls(detached["a"]);await Task.Delay(40);UiCheck(controls!.IsVisible&&controls.Owner==detached["a"],"Settings remains accessible and correctly owned from a locked detached window");
        Capture(detached["a"],Path.Combine(output,"detached-a.png"));Capture(detached["b"],Path.Combine(output,"detached-b.png"));
        detached["a"].Width=520;await Task.Delay(50);UiCheck(detached["b"].Width!=520,"Detached window dimensions are independent");
        Reattach();await Task.Delay(80);UiCheck(!layout.Detached&&detached.Count==0&&IsVisible&&captions.Children.Count==2,"Reattach restores the existing combined host and both panels");
        UiCheck(panels["a"].Following==wasFollowing&&(wasFollowing||combinedMark is {} rm&&Math.Abs((panels["a"].MarkY(rm.Key,rm.Index)??double.MaxValue)-rm.Y)<1),"Reattachment retains the combined reader's surviving word");
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
        await ReadingViewportSmoke(output);
    }
    async Task ReadingViewportSmoke(string output){
        var sample=new CaptionState();
        void Caption(string slot,long id,string text,bool final,long revision)=>sample.Apply(JsonSerializer.Serialize(new{type="caption",source=new{id=slot,generation=slot=="a"?1:2,name="Synthetic "+slot,identity="reading-"+slot},id,text,final,revision,capturedAt=id}));
        var a=new TranscriptPanel("a",true);var b=new TranscriptPanel("b",true);
        var wa=new Window{Width=450,Height=290,Content=a,Owner=this,ShowInTaskbar=false};var wb=new Window{Width=450,Height=290,Content=b,Owner=this,ShowInTaskbar=false};
        wa.Show();wb.Show();
        void Paint(double size=22){a.Update(sample,size,"#80DFFF",false);b.Update(sample,size,"#FFD68A",false);}
        string Words(int count)=>string.Join(" ",Enumerable.Repeat("synthetic readable words",count));
        for(int i=1;i<=12;i++)Caption("a",i,"Retained synthetic sentence "+i+" "+Words(3),true,1);
        Caption("b",1,Words(15),false,1);Paint();await Task.Delay(80);
        for(int i=1;i<=7;i++){
            string prefix=i<=3?"First draft":"Revised earlier words";
            Caption("a",13,prefix+" "+Words(i*5),false,i);Paint();await Task.Delay(60);
            UiCheck(a.Following&&a.LatestVisible(),"Continuous speech follows through prefix and non-prefix revision "+i);
        }
        Caption("a",13,"Accurately corrected shorter sentence.",false,8);Paint();await Task.Delay(70);UiCheck(a.Following&&a.LatestVisible(),"A shorter correction keeps its latest words visible");
        Caption("a",13,"Accurately corrected final sentence.",true,9);Paint();await Task.Delay(70);UiCheck(a.Following&&a.LatestVisible(),"Partial-to-final correction keeps the latest line visible");
        double silence=a.Text.VerticalOffset;string retained=a.PlainText;await Task.Delay(100);Paint();await Task.Delay(50);
        UiCheck(a.Text.VerticalOffset==silence&&a.PlainText==retained,"Silence requires no new event or scroll adjustment to retain native text");
        a.ReaderNavigating();a.Scroll.ScrollToVerticalOffset(a.ScrollableHeight*.35);await Task.Delay(80);var mark=a.ReadingMark()??throw new Exception("Missing rereading anchor");
        UiCheck(!a.Following&&b.Following,"Manual rereading is independent for two native panels");
        Caption("a",14,"New speech "+Words(50),false,1);Caption("b",1,"Corrected other-source speech "+Words(60),false,2);Paint();await Task.Delay(80);
        UiCheck(!a.Following&&Math.Abs((a.MarkY(mark.Key,mark.Index)??double.MaxValue)-mark.Y)<1&&b.LatestVisible(),"A stays on its reading word while B independently follows a non-prefix correction");
        var selectionStart=a.Text.GetPositionFromPoint(new Point(4,4),true)??throw new Exception("Missing selectable native reading word");
        a.Text.Selection.Select(selectionStart,selectionStart.GetPositionAtOffset(12)??selectionStart);string selection=a.Text.Selection.Text;
        Caption("b",1,"Another revision "+Words(65),false,3);Paint();await Task.Delay(60);
        UiCheck(selection.Length>0&&a.Text.Selection.Text==selection&&!a.Following,"Other-source updates preserve selected reading text without stealing reader follow intent");
        mark=a.ReadingMark()??throw new Exception("Missing hidden reader fixture");wa.Hide();Caption("a",14,"Revised while hidden "+Words(55),false,2);Paint();await Task.Delay(60);wa.Show();await Task.Delay(80);
        UiCheck(!a.Following&&Math.Abs((a.MarkY(mark.Key,mark.Index)??double.MaxValue)-mark.Y)<1,"Hide/Show keeps a rereading anchor while recognition continues");
        a.ReaderNavigating();a.Scroll.ScrollToEnd();await Task.Delay(80);Caption("a",14,"Rewritten new speech "+Words(65),false,3);Paint();await Task.Delay(80);
        UiCheck(a.Following&&a.LatestVisible(),"Manually returning to the bottom resumes native follow within the same utterance");
        wa.Hide();Caption("a",14,"Following while hidden "+Words(70),false,4);Paint();await Task.Delay(60);wa.Show();await Task.Delay(80);UiCheck(a.Following&&a.LatestVisible(),"Hide/Show resumes the live tail without a new speech event");
        Caption("a",14,"Final synthetic speech",true,5);
        for(int i=15;i<=215;i++)Caption("a",i,"Retained synthetic sentence "+i+" "+Words(2),true,1);Paint();await Task.Delay(90);
        UiCheck(sample.History.Count(c=>c.Source.Id=="a")==200&&a.LatestVisible(),"History pruning at the bottom preserves the independent 200-segment budget and latest line");
        a.ReaderNavigating();a.Scroll.ScrollToVerticalOffset(a.ScrollableHeight*.5);await Task.Delay(80);mark=a.ReadingMark()??throw new Exception("Missing pruning anchor");
        for(int i=216;i<=222;i++)Caption("a",i,"Retained synthetic sentence "+i+" "+Words(2),true,1);Paint();await Task.Delay(90);
        UiCheck(!a.Following&&Math.Abs((a.MarkY(mark.Key,mark.Index)??double.MaxValue)-mark.Y)<1,"Pruning earlier history preserves a surviving native reading word");
        int count=sample.History.Count;string all=a.PlainText;
        foreach(var size in new[]{(700d,520d),(380d,260d),(720d,540d),(400d,280d)}){
            mark=a.ReadingMark()??throw new Exception("Missing resize anchor");wa.Width=size.Item1;wa.Height=size.Item2;await Task.Delay(80);
            UiCheck(!a.Following&&Math.Abs((a.MarkY(mark.Key,mark.Index)??double.MaxValue)-mark.Y)<1,"Small/large native reflow retains the reader's word and follow intent");
            UiCheck(a.PlainText==all&&sample.History.Count==count,"Window resizing never deletes retained caption history");
        }
        mark=a.ReadingMark()??throw new Exception("Missing font anchor");Paint(30);await Task.Delay(90);UiCheck(!a.Following&&Math.Abs((a.MarkY(mark.Key,mark.Index)??double.MaxValue)-mark.Y)<1,"Font-size reflow preserves a surviving native reading anchor");
        a.ReaderNavigating();a.Scroll.ScrollToEnd();await Task.Delay(80);
        foreach(var size in new[]{(700d,520d),(380d,260d),(720d,540d)}){wa.Width=size.Item1;wa.Height=size.Item2;await Task.Delay(80);UiCheck(a.Following&&a.LatestVisible()&&sample.History.Count==count,"Following resize reveals retained older text without losing newest speech or history");}
        Caption("a",223,"Unfinished synthetic recognition "+Words(25),false,1);Paint(30);await Task.Delay(80);double offset=a.Text.VerticalOffset;string draft=a.PlainText;
        sample.Apply("""{"type":"segment-retired","source":{"id":"a","generation":1,"name":"Synthetic a","identity":"reading-a"},"id":223,"revision":1,"terminal":false,"retain":true}""");Paint(30);await Task.Delay(80);
        UiCheck(!a.IsLive&&a.PlainText==draft&&a.Text.VerticalOffset==offset,"Unfinished expiration changes classification without erasing text or scrolling");
        UiCheck(a.Text.Document.PagePadding.Bottom<=a.Text.ViewportHeight,"Anchor preservation never allocates multiple blank viewports");
        a.ReaderNavigating();a.Scroll.ScrollToVerticalOffset(a.ScrollableHeight*.25);await Task.Delay(80);mark=a.ReadingMark()??throw new Exception("Missing removed-anchor fixture");
        for(int i=224;i<=450;i++)Caption("a",i,"New retained synthetic sentence "+i+" "+Words(2),true,1);Paint(30);await Task.Delay(90);
        UiCheck(a.MarkY(mark.Key,mark.Index)==null&&!a.Following&&double.IsFinite(a.Text.VerticalOffset)&&a.Text.VerticalOffset<=a.ScrollableHeight+1,"Pruning the reading word recovers to retained text without reviving deleted segments or forcing live follow");
        File.WriteAllText(Path.Combine(output,"caption-reading-geometry.json"),JsonSerializer.Serialize(new{a=a.Geometry(),b=b.Geometry()},new JsonSerializerOptions{WriteIndented=true}));
        wa.Close();wb.Close();
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
        panel.ReaderNavigating();panel.Scroll.ScrollToVerticalOffset(Math.Max(0,panel.Scroll.ScrollableHeight-20));await Task.Delay(70);
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
