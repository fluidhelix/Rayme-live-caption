using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;

namespace Rayme.Overlay;

// One continuous selectable paragraph per source. Runs retain protocol segment identity.
// Health/LIVE updates never mutate the document or request a scroll.
internal sealed class TranscriptPanel : Border {
    readonly TextBlock label=Theme.Text("",16,strong:true),live=Theme.Text("LIVE",10,"#E3E3E3",true);
    internal readonly RichTextBox Text=new(){IsReadOnly=true,IsReadOnlyCaretVisible=false,IsUndoEnabled=false,AcceptsTab=false,Background=Brushes.Transparent,Foreground=Theme.Brush("#F2F2F2"),BorderThickness=new(0),Padding=new(0),VerticalScrollBarVisibility=ScrollBarVisibility.Visible,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,FontStyle=FontStyles.Normal,FontWeight=FontWeights.Normal,SelectionBrush=Theme.Brush("#555555")};
    readonly Paragraph paragraph=new(){Margin=new(0)};
    readonly Dictionary<string,Run> runs=[];
    TranscriptSegment[] segments=[];
    ScrollFollow follow=new();
    string identity="";
    Anchor? lastAnchor,pendingAnchor;
    bool pending,suppressScroll,settling;
    double pendingOffset,previousExtent;
    long pendingRevision;
    bool pendingAppend;
    bool lastSetup;
    readonly bool traceEnabled;
    readonly Queue<object> geometry=[];
    sealed record Anchor(string Key,int Index,double Y,string OldText,int Ordinal);
    internal string Slot {get;}
    internal bool IsLive=>live.Visibility==Visibility.Visible;
    internal string SourceLabel=>label.Text;
    internal string FinalText=>string.Join(" ",segments.Where(x=>x.Final).Select(x=>x.Text));
    internal string PartialText=>string.Join(" ",segments.Where(x=>!x.Final).Select(x=>x.Text));
    internal string PlainText=>new TextRange(paragraph.ContentStart,paragraph.ContentEnd).Text;
    internal double ScrollableHeight=>Math.Max(0,Text.ExtentHeight-Text.ViewportHeight);
    internal ScrollViewer Scroll {get{Text.ApplyTemplate();return Descendant<ScrollViewer>(Text)??throw new InvalidOperationException("Transcript scroll viewer unavailable");}}
    static T? Descendant<T>(DependencyObject parent) where T:DependencyObject {for(int i=0;i<VisualTreeHelper.GetChildrenCount(parent);i++){var child=VisualTreeHelper.GetChild(parent,i);if(child is T value)return value;if(Descendant<T>(child) is {} nested)return nested;}return null;}
    public TranscriptPanel(string slot,bool trace=false){
        Slot=slot;traceEnabled=trace;Padding=new(18,0,8,8);BorderThickness=new(0);Background=Brushes.Transparent;
        var grid=new Grid();grid.RowDefinitions.Add(new(){Height=new GridLength(34)});grid.RowDefinitions.Add(new());
        var header=new DockPanel();
        var badge=new Border{Width=40,Height=18,CornerRadius=new(9),Background=Theme.Brush("#252525"),Child=live,VerticalAlignment=VerticalAlignment.Center,Margin=new(8,0,4,0)};
        live.HorizontalAlignment=HorizontalAlignment.Center;live.VerticalAlignment=VerticalAlignment.Center;
        // Reserve the entire badge's layout even when not live.
        live.Visibility=Visibility.Hidden;badge.SetBinding(OpacityProperty,new System.Windows.Data.Binding("Visibility"){Source=live,Converter=new LiveOpacity()});
        DockPanel.SetDock(badge,Dock.Right);header.Children.Add(badge);label.TextWrapping=TextWrapping.NoWrap;label.TextTrimming=TextTrimming.CharacterEllipsis;label.VerticalAlignment=VerticalAlignment.Center;header.Children.Add(label);grid.Children.Add(header);
        Text.Document=new FlowDocument(paragraph){PagePadding=new(0),Foreground=Theme.Brush("#F2F2F2"),FontFamily=new FontFamily("Segoe UI Variable Text, Segoe UI")};Text.Tag=this;var menu=new ContextMenu();menu.Items.Add(new MenuItem{Header="Copy",Command=ApplicationCommands.Copy,CommandTarget=Text});menu.Items.Add(new MenuItem{Header="Select all",Command=ApplicationCommands.SelectAll,CommandTarget=Text});Text.ContextMenu=menu;Theme.Accessible(Text,slot=="device"?"Playback transcript":"Application "+slot.ToUpperInvariant()+" transcript");Text.SetResourceReference(Control.FocusVisualStyleProperty,"KeyboardFocus");
        Text.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((_,e)=>{
            if(!suppressScroll&&e.ExtentHeightChange==0&&e.ViewportHeightChange==0){follow.ReaderMoved(Text.VerticalOffset,Math.Max(0,Text.ExtentHeight-Text.Document.PagePadding.Bottom-Text.ViewportHeight));lastAnchor=Capture();}
        }));
        Text.PreviewMouseWheel+=(_,_)=>ReaderNavigating();
        Text.PreviewKeyDown+=(_,e)=>{if(e.Key is Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End)ReaderNavigating();};
        Text.PreviewMouseDown+=(_,e)=>{if(WindowDrag.Ancestor<ScrollBar>(e.OriginalSource as DependencyObject)!=null)ReaderNavigating();};
        SizeChanged+=(_,_)=>{if(lastAnchor!=null&&!pending&&!settling)QueueRestore(lastAnchor,false,Text.VerticalOffset,Text.ExtentHeight);};
        Grid.SetRow(Text,1);grid.Children.Add(Text);Child=grid;
    }
    sealed class LiveOpacity:System.Windows.Data.IValueConverter {
        public object Convert(object value,Type t,object p,System.Globalization.CultureInfo c)=>value is Visibility.Visible?1d:0d;
        public object ConvertBack(object v,Type t,object p,System.Globalization.CultureInfo c)=>throw new NotSupportedException();
    }
    void ReaderNavigating(){follow.ReaderMoved(Text.VerticalOffset,Math.Max(0,Text.ExtentHeight-Text.Document.PagePadding.Bottom-Text.ViewportHeight));suppressScroll=false;}
    Anchor? Capture(){
        if(!IsLoaded||runs.Count==0)return null;
        try{
            var position=Text.GetPositionFromPoint(new Point(2,2),true);if(position==null)return null;
            var ordered=paragraph.Inlines.OfType<Run>().ToArray();
            for(int i=0;i<ordered.Length;i++){
                var run=ordered[i];if(position.CompareTo(run.ContentEnd)>0)continue;
                int offset=Math.Clamp(run.ContentStart.GetOffsetToPosition(position),0,run.Text.Length);var point=run.ContentStart.GetPositionAtOffset(offset)??run.ContentStart;
                var rect=point.GetCharacterRect(LogicalDirection.Forward);if(rect.IsEmpty)return null;
                return new((string)run.Tag,offset,rect.Top,run.Text,i);
            }
        }catch(InvalidOperationException){}
        return null;
    }
    TextPointer? Resolve(Anchor? anchor){
        if(anchor==null||runs.Count==0)return null;
        if(runs.TryGetValue(anchor.Key,out var run)){int index=TextPosition.Map(anchor.OldText,run.Text,anchor.Index);return run.ContentStart.GetPositionAtOffset(index)??run.ContentStart;}
        // History trimming or retirement may remove the anchor. Use a surviving
        // neighboring segment, without retaining canceled transcript bytes.
        var values=paragraph.Inlines.OfType<Run>().ToArray();return values[Math.Clamp(anchor.Ordinal,0,values.Length-1)].ContentStart;
    }
    void QueueRestore(Anchor? anchor,bool append,double offset,double extent){
        if(!pending){pendingAnchor=anchor;pendingOffset=offset;previousExtent=extent;pendingRevision=follow.Revision;pendingAppend=append;pending=true;
            Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(Settle));
        }else pendingAppend|=append;
    }
    void Settle(){
        if(!pending)return;pending=false;
        if(!IsLoaded){suppressScroll=false;return;}
        if(follow.Revision!=pendingRevision){Text.Document.PagePadding=new(0);return;}
        settling=true;suppressScroll=true;Text.UpdateLayout();
        if(pendingAppend&&follow.Following){Text.Document.PagePadding=new(0);Text.UpdateLayout();Text.ScrollToEnd();}
        else{
            var point=Resolve(pendingAnchor);double target=pendingOffset;
            if(point!=null){var rect=point.GetCharacterRect(LogicalDirection.Forward);if(!rect.IsEmpty)target=Text.VerticalOffset+rect.Top-(pendingAnchor?.Y??rect.Top);}
            target=Math.Max(0,target);
            double natural=Math.Max(0,Text.ExtentHeight-Text.Document.PagePadding.Bottom);
            double cushion=Math.Max(0,target+Text.ViewportHeight-natural);
            Text.Document.PagePadding=new Thickness(0,0,0,Math.Min(cushion,Math.Max(previousExtent,Text.ViewportHeight)));
            Text.UpdateLayout();Text.ScrollToVerticalOffset(target);
        }
        Trace("settled");pendingAnchor=null;settling=false;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>{suppressScroll=false;lastAnchor=Capture();}));
    }
    internal void PrepareMove(){lastAnchor=Capture()??lastAnchor;QueueRestore(lastAnchor,false,Text.VerticalOffset,Text.ExtentHeight);}
    internal void Interactions(bool locked){Cursor=locked?Cursors.Arrow:Cursors.SizeAll;Text.Cursor=Cursors.IBeam;}
    internal void Update(CaptionState state,double size,string color,bool setup){
        var projection=TranscriptPresentation.Build(state,Slot);Theme.Accessible(Text,projection.Label+" transcript");label.Text=projection.Label;label.ToolTip=projection.Label;label.Foreground=Theme.Brush(color);live.Visibility=projection.Live?Visibility.Visible:Visibility.Hidden;
        var next=TranscriptPresentation.Segments(state,Slot);
        string nextIdentity=state.Sources.GetValueOrDefault(Slot)?.Source.Identity??"";
        if(identity!=nextIdentity){identity=nextIdentity;lastAnchor=pendingAnchor=null;follow=new();pending=false;Text.Document.PagePadding=new(0);Text.ScrollToHome();}
        bool changed=!segments.SequenceEqual(next)||Text.FontSize!=size||next.Length==0&&lastSetup!=setup;
        if(!changed)return;
        var anchor=pending?pendingAnchor:Capture();double offset=Text.VerticalOffset,extent=Text.ExtentHeight;
        bool append=TextPosition.IsAppend(segments,next);
        Trace("before-update");suppressScroll=true;
        // Reserve enough end space BEFORE mutation to prevent offset clamping.
        // Settle reduces it to the exact space needed by the surviving text anchor.
        Text.Document.PagePadding=new Thickness(0,0,0,Math.Max(Text.ViewportHeight,offset+Text.ViewportHeight));
        Text.BeginChange();
        try{
            var keys=next.Select(s=>s.Key).ToHashSet();foreach(var key in runs.Keys.Where(k=>!keys.Contains(k)).ToArray()){paragraph.Inlines.Remove(runs[key]);runs.Remove(key);}
            if(runs.Count==0)paragraph.Inlines.Clear();
            Run? preceding=null;
            for(int i=0;i<next.Length;i++){
                var segment=next[i];if(!runs.TryGetValue(segment.Key,out var run)){run=new Run{Tag=segment.Key};runs[segment.Key]=run;}
                string value=(i>0?" ":"")+segment.Text;if(run.Text!=value)run.Text=value;
                Inline? desired=preceding==null?paragraph.Inlines.FirstInline:preceding.NextInline;
                if(!ReferenceEquals(desired,run)){if(run.Parent!=null)paragraph.Inlines.Remove(run);if(preceding==null){if(paragraph.Inlines.FirstInline is {} first)paragraph.Inlines.InsertBefore(first,run);else paragraph.Inlines.Add(run);}else paragraph.Inlines.InsertAfter(preceding,run);}
                preceding=run;
            }
            if(next.Length==0)paragraph.Inlines.Add(new Run(setup?"Complete speech setup to begin.":state.Sources.ContainsKey(Slot)?"Listening for conversation…":"Choose an audio source in Controls."));
            Text.FontSize=size;Text.Document.FontSize=size;paragraph.LineHeight=size*1.4;paragraph.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;
            segments=next;lastSetup=setup;
        }finally{Text.EndChange();}
        QueueRestore(anchor,append,offset,extent);
    }
    internal bool HasTextAt(Point point){
        // Leave text, inter-word whitespace and a short trailing selection area to
        // the native editor. Only truly blank caption surface initiates dragging.
        var p=Text.GetPositionFromPoint(point,false);if(p==null)return false;
        var start=p.GetLineStartPosition(0);if(start==null)return false;
        var end=p.GetLineStartPosition(1)?.GetNextInsertionPosition(LogicalDirection.Backward)??paragraph.ContentEnd;
        var left=start.GetCharacterRect(LogicalDirection.Forward);var right=end.GetCharacterRect(LogicalDirection.Backward);
        return !left.IsEmpty&&!right.IsEmpty&&point.Y>=left.Top&&point.Y<=left.Bottom&&point.X<=right.Right+Text.FontSize*2;
    }
    void Trace(string reason){if(!traceEnabled)return;geometry.Enqueue(new{reason,slot=Slot,offset=Text.VerticalOffset,extent=Text.ExtentHeight,viewport=Text.ViewportHeight,width=Text.ViewportWidth,following=follow.Following});while(geometry.Count>128)geometry.Dequeue();}
    internal (string Key,int Index,double Y)? ReadingMark(){var a=Capture();return a==null?null:(a.Key,a.Index,a.Y);}
    internal double? MarkY(string key,int index){if(!runs.TryGetValue(key,out var run))return null;var p=run.ContentStart.GetPositionAtOffset(Math.Clamp(index,0,run.Text.Length));var r=p?.GetCharacterRect(LogicalDirection.Forward);return r is {} rect&&!rect.IsEmpty?rect.Top:null;}
    internal object[] Geometry()=>geometry.ToArray();
}
