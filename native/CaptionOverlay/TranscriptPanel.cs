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
    bool pending,settling,readerNavigating,readerSelecting,thumbDragging,pendingReflow;
    double pendingOffset,readerStartOffset;
    long pendingRevision;
    bool lastSetup;
    readonly bool traceEnabled;
    readonly Queue<object> geometry=[];
    sealed record Anchor(string Key,int Index,double Y,string OldText,string[] Neighbors);
    static readonly HashSet<ICommand> selectionCommands=[ApplicationCommands.SelectAll,
        EditingCommands.SelectLeftByCharacter,EditingCommands.SelectRightByCharacter,
        EditingCommands.SelectLeftByWord,EditingCommands.SelectRightByWord,
        EditingCommands.SelectUpByLine,EditingCommands.SelectDownByLine,
        EditingCommands.SelectUpByParagraph,EditingCommands.SelectDownByParagraph,
        EditingCommands.SelectUpByPage,EditingCommands.SelectDownByPage,
        EditingCommands.SelectToLineStart,EditingCommands.SelectToLineEnd,
        EditingCommands.SelectToDocumentStart,EditingCommands.SelectToDocumentEnd];
    static readonly HashSet<ICommand> navigationCommands=[
        EditingCommands.MoveLeftByCharacter,EditingCommands.MoveRightByCharacter,
        EditingCommands.MoveLeftByWord,EditingCommands.MoveRightByWord,
        EditingCommands.MoveUpByLine,EditingCommands.MoveDownByLine,
        EditingCommands.MoveUpByParagraph,EditingCommands.MoveDownByParagraph,
        EditingCommands.MoveUpByPage,EditingCommands.MoveDownByPage,
        EditingCommands.MoveToLineStart,EditingCommands.MoveToLineEnd,
        EditingCommands.MoveToDocumentStart,EditingCommands.MoveToDocumentEnd,
        ScrollBar.LineUpCommand,ScrollBar.LineDownCommand,ScrollBar.PageUpCommand,ScrollBar.PageDownCommand,
        ScrollBar.ScrollToTopCommand,ScrollBar.ScrollToBottomCommand,ScrollBar.ScrollToHomeCommand,ScrollBar.ScrollToEndCommand,
        ScrollBar.ScrollToVerticalOffsetCommand,ScrollBar.DeferScrollToVerticalOffsetCommand];
    static RoutedCommand? ViewportKey(Key key,ModifierKeys modifiers){
        if(modifiers!=ModifierKeys.None&&!(modifiers==ModifierKeys.Control&&key is Key.Home or Key.End))return null;
        return key switch{Key.Up=>ScrollBar.LineUpCommand,Key.Down=>ScrollBar.LineDownCommand,
            Key.PageUp=>ScrollBar.PageUpCommand,Key.PageDown=>ScrollBar.PageDownCommand,
            Key.Home=>ScrollBar.ScrollToTopCommand,Key.End=>ScrollBar.ScrollToBottomCommand,_=>null};
    }
    internal string Slot {get;}
    internal bool IsLive=>live.Visibility==Visibility.Visible&&live.Text=="LIVE";
    internal string SourceLabel=>label.Text;
    internal string FinalText=>string.Join(" ",segments.Where(x=>x.Final).Select(x=>x.Text));
    internal string PartialText=>string.Join(" ",segments.Where(x=>!x.Final&&!x.Unfinished).Select(x=>x.Text));
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
        // ScrollChanged is evidence of movement, never evidence of reader intent.
        // WPF also raises it for layout, extent clamping and our own restoration.
        Text.AddHandler(ScrollViewer.ScrollChangedEvent,new ScrollChangedEventHandler((_,_)=>{if(readerNavigating)ScheduleReaderPosition();}));
        Text.PreviewMouseWheel+=(_,_)=>ReaderNavigating(reason:"wheel");
        Text.AddHandler(Keyboard.PreviewKeyDownEvent,new KeyEventHandler((_,e)=>{
            bool selectAll=e.Key==Key.A&&(Keyboard.Modifiers&ModifierKeys.Control)!=0;
            if(selectAll||e.Key is Key.Left or Key.Right or Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Home or Key.End or Key.Space)
                ReaderNavigating(selectAll||(Keyboard.Modifiers&ModifierKeys.Shift)!=0,"key:"+e.Key);
            // WPF disables caret-navigation commands when a read-only caret is
            // hidden. Route plain viewport keys to its ScrollViewer instead;
            // Shift-selection and other editor commands keep their native path.
            var scrolling=Text.IsReadOnly&&!Text.IsReadOnlyCaretVisible?ViewportKey(e.Key,Keyboard.Modifiers):null;
            if(!e.Handled&&scrolling!=null&&scrolling.CanExecute(null,Scroll)){scrolling.Execute(null,Scroll);e.Handled=true;}
        }),true);
        // SelectAll from the context menu and the editor's Shift/Ctrl+Shift
        // bindings use routed commands, including commands invoked by assistive UI.
        Text.AddHandler(CommandManager.PreviewExecutedEvent,new ExecutedRoutedEventHandler((_,e)=>{
            if(selectionCommands.Contains(e.Command))ReaderNavigating(true,"selection:"+((RoutedCommand)e.Command).Name);
            // Embedded scrollbar buttons route their commands to ScrollViewer;
            // unlike standalone bars, they need not raise ScrollBar.Scroll.
            else if(navigationCommands.Contains(e.Command))ReaderNavigating(reason:"navigation:"+((RoutedCommand)e.Command).Name);
        }),true);
        Text.PreviewMouseDown+=(_,e)=>{if(WindowDrag.Ancestor<ScrollBar>(e.OriginalSource as DependencyObject)!=null)ReaderNavigating();else if(HasTextAt(e.GetPosition(Text)))ReaderNavigating(true);};
        Text.AddHandler(ScrollBar.ScrollEvent,new ScrollEventHandler((_,e)=>ReaderNavigating(reason:"scrollbar:"+e.ScrollEventType)),true);
        Text.AddHandler(Thumb.DragStartedEvent,new DragStartedEventHandler((_,_)=>{thumbDragging=true;ReaderNavigating();}),true);
        Text.AddHandler(Thumb.DragCompletedEvent,new DragCompletedEventHandler((_,_)=>{thumbDragging=false;ScheduleReaderPosition();}),true);
        Text.PreviewMouseUp+=(_,_)=>ScheduleReaderPosition();
        Text.LostMouseCapture+=(_,_)=>{thumbDragging=false;ScheduleReaderPosition();};
        SizeChanged+=(_,_)=>{if(!settling)QueueRestore(lastAnchor,Text.VerticalOffset,true);};
        Loaded+=(_,_)=>QueueRestore(lastAnchor,Text.VerticalOffset,true);
        Unloaded+=(_,_)=>{readerNavigating=thumbDragging=false;navigationTicket++;};
        IsVisibleChanged+=(_,_)=>{if(IsVisible)QueueRestore(lastAnchor,Text.VerticalOffset,true);};
        Grid.SetRow(Text,1);grid.Children.Add(Text);Child=grid;
    }
    sealed class LiveOpacity:System.Windows.Data.IValueConverter {
        public object Convert(object value,Type t,object p,System.Globalization.CultureInfo c)=>value is Visibility.Visible?1d:0d;
        public object ConvertBack(object v,Type t,object p,System.Globalization.CultureInfo c)=>throw new NotSupportedException();
    }
    internal bool Following=>follow.Following;
    internal bool ScrollWorkPending=>pending||settling||readerNavigating;
    long navigationTicket;
    internal void ReaderNavigating(bool selecting=false,string reason="reader-input"){readerStartOffset=Text.VerticalOffset;readerNavigating=true;readerSelecting=selecting;follow.BeginNavigation();Trace(reason);ScheduleReaderPosition();}
    void ScheduleReaderPosition(){
        if(!readerNavigating)return;
        long ticket=++navigationTicket;
        Dispatcher.BeginInvoke(DispatcherPriority.ContextIdle,new Action(()=>{
            if(ticket!=navigationTicket||!IsLoaded)return;
            // A held thumb or text-selection drag may keep moving between events.
            // A single Windows wheel step is commonly 48 DIPs, inside the
            // existing 60-DIP resume band. Moving upward is still rereading;
            // only stationary/downward navigation may rearm near-bottom follow.
            bool movingAway=Text.VerticalOffset<readerStartOffset-.5;
            follow.ReaderMoved(Text.VerticalOffset,Math.Max(0,Text.ExtentHeight-Text.Document.PagePadding.Bottom-Text.ViewportHeight),readerSelecting&&!Text.Selection.IsEmpty,movingAway);lastAnchor=Capture();Trace("reader-position");
            if(!thumbDragging&&(Mouse.LeftButton!=MouseButtonState.Pressed||!Text.IsMouseCaptureWithin))readerNavigating=false;
            if(follow.Following&&!readerNavigating)QueueRestore(lastAnchor,Text.VerticalOffset,true);
        }));
    }
    Anchor? Capture(){
        if(!IsLoaded||!IsVisible||runs.Count==0)return null;
        try{
            var position=Text.GetPositionFromPoint(new Point(2,2),true);if(position==null)return null;
            var ordered=paragraph.Inlines.OfType<Run>().ToArray();
            for(int i=0;i<ordered.Length;i++){
                var run=ordered[i];if(position.CompareTo(run.ContentEnd)>0)continue;
                int offset=Math.Clamp(run.ContentStart.GetOffsetToPosition(position),0,run.Text.Length);var point=run.ContentStart.GetPositionAtOffset(offset)??run.ContentStart;
                var rect=point.GetCharacterRect(LogicalDirection.Forward);if(rect.IsEmpty)return null;
                return new((string)run.Tag,offset,rect.Top,run.Text,ordered.Skip(i+1).Concat(ordered.Take(i).Reverse()).Select(r=>(string)r.Tag).ToArray());
            }
        }catch(InvalidOperationException){}
        return null;
    }
    TextPointer? Resolve(Anchor? anchor){
        if(anchor==null||runs.Count==0)return null;
        if(runs.TryGetValue(anchor.Key,out var run)){int index=TextPosition.Map(anchor.OldText,run.Text,anchor.Index);return run.ContentStart.GetPositionAtOffset(index)??run.ContentStart;}
        // History trimming or retirement may remove the anchor. Use a surviving
        // neighboring segment, without retaining canceled transcript bytes.
        foreach(string key in anchor.Neighbors)if(runs.TryGetValue(key,out var neighbor))return neighbor.ContentStart;
        return paragraph.Inlines.OfType<Run>().FirstOrDefault()?.ContentStart;
    }
    void QueueRestore(Anchor? anchor,double offset,bool reflow=false){
        if(pending){pendingReflow|=reflow;return;}
        pendingReflow=reflow;
        pendingAnchor=anchor;pendingOffset=offset;pendingRevision=follow.Revision;pending=true;
        Dispatcher.BeginInvoke(DispatcherPriority.Loaded,new Action(Settle));
    }
    void Settle(){
        if(!pending)return;pending=false;
        if(!IsLoaded||!IsVisible||readerNavigating||follow.Revision!=pendingRevision)return;
        settling=true;
        try{
            Text.UpdateLayout();
            if(follow.Following){
                if(pendingReflow){Text.Document.PagePadding=new(0);Text.UpdateLayout();}
                else RestoreAnchor();
                var end=paragraph.Inlines.OfType<Run>().LastOrDefault()?.ContentEnd;
                var rect=end?.GetCharacterRect(LogicalDirection.Backward);
                // Revisions need not be prefix appends. Only move when the actual
                // latest line is outside the viewport, not on status/LIVE changes.
                if(rect is {} r&&!r.IsEmpty&&follow.NeedsReveal(r.Top,r.Bottom,Text.ViewportHeight)){
                    Text.Document.PagePadding=new(0);Text.UpdateLayout();
                    rect=end?.GetCharacterRect(LogicalDirection.Backward);
                    if(rect is {} latest&&!latest.IsEmpty)Text.ScrollToVerticalOffset(Math.Clamp(Text.VerticalOffset+latest.Bottom-Text.ViewportHeight,0,ScrollableHeight));
                }
            }else RestoreAnchor();
            Text.UpdateLayout();
            Trace("settled");lastAnchor=Capture();pendingAnchor=null;
        }finally{settling=false;}
    }
    void RestoreAnchor(){
        var point=Resolve(pendingAnchor);double target=pendingOffset;
        if(point!=null){var rect=point.GetCharacterRect(LogicalDirection.Forward);if(!rect.IsEmpty)target=Text.VerticalOffset+rect.Top-(pendingAnchor?.Y??rect.Top);}
        target=Math.Max(0,target);
        double natural=Math.Max(0,Text.ExtentHeight-Text.Document.PagePadding.Bottom);
        // At most one viewport of end slack, never multiple blank pages. This is
        // released on live growth, reader return to bottom and following reflow.
        double cushion=pendingAnchor==null?0:Math.Clamp(target+Text.ViewportHeight-natural,0,Text.ViewportHeight);
        Text.Document.PagePadding=new Thickness(0,0,0,cushion);Text.UpdateLayout();Text.ScrollToVerticalOffset(Math.Min(target,ScrollableHeight));
        // WPF queues the offset change. Measure the latest line only after that
        // request is applied; pre-scroll glyph bounds can hide a clipped tail.
        Text.UpdateLayout();Trace("anchor-applied");
    }
    internal void PrepareMove(){lastAnchor=Capture()??lastAnchor;QueueRestore(lastAnchor,Text.VerticalOffset);}
    internal void Interactions(bool locked){Cursor=locked?Cursors.Arrow:Cursors.SizeAll;Text.Cursor=Cursors.IBeam;}
    internal void Update(CaptionState state,double size,string color,bool setup){
        var projection=TranscriptPresentation.Header(state,Slot);Theme.Accessible(Text,projection.Label+" transcript");label.Text=projection.Label;label.ToolTip=projection.Label;label.Foreground=Theme.Brush(color);live.Text=projection.Live?"LIVE":"DRAFT";live.ToolTip=projection.Live?"Recognition is updating":"Underlined text is unfinished recognition retained for reading, not a verified final.";live.Visibility=projection.Live||projection.Unfinished?Visibility.Visible:Visibility.Hidden;
        var next=TranscriptPresentation.Segments(state,Slot);
        string nextIdentity=state.Sources.GetValueOrDefault(Slot)?.Source.Identity??"";
        bool resetView=identity!=nextIdentity||segments.Length>0&&next.Length==0;
        if(resetView){identity=nextIdentity;lastAnchor=pendingAnchor=null;follow=new();pending=false;readerNavigating=readerSelecting=thumbDragging=false;navigationTicket++;Text.Document.PagePadding=new(0);Text.ScrollToHome();}
        bool fontChanged=Text.FontSize!=size;
        bool changed=!segments.SequenceEqual(next)||fontChanged||next.Length==0&&lastSetup!=setup;
        bool geometryChanged=fontChanged||!segments.Select(s=>(s.Key,s.Text)).SequenceEqual(next.Select(s=>(s.Key,s.Text)))||next.Length==0&&lastSetup!=setup;
        if(!changed)return;
        var anchor=resetView?null:pending?pendingAnchor:Capture()??lastAnchor;double offset=resetView?0:Text.VerticalOffset;
        Trace("before-update");
        Text.BeginChange();
        try{
            var keys=next.Select(s=>s.Key).ToHashSet();foreach(var key in runs.Keys.Where(k=>!keys.Contains(k)).ToArray()){paragraph.Inlines.Remove(runs[key]);runs.Remove(key);}
            if(runs.Count==0)paragraph.Inlines.Clear();
            Run? preceding=null;
            for(int i=0;i<next.Length;i++){
                var segment=next[i];if(!runs.TryGetValue(segment.Key,out var run)){run=new Run{Tag=segment.Key};runs[segment.Key]=run;}
                string value=(i>0?" ":"")+segment.Text;if(run.Text!=value)run.Text=value;
                run.ToolTip=segment.Unfinished?"Unfinished recognition retained for reading; not a verified final.":null;run.TextDecorations=segment.Unfinished?TextDecorations.Underline:null;
                Inline? desired=preceding==null?paragraph.Inlines.FirstInline:preceding.NextInline;
                if(!ReferenceEquals(desired,run)){if(run.Parent!=null)paragraph.Inlines.Remove(run);if(preceding==null){if(paragraph.Inlines.FirstInline is {} first)paragraph.Inlines.InsertBefore(first,run);else paragraph.Inlines.Add(run);}else paragraph.Inlines.InsertAfter(preceding,run);}
                preceding=run;
            }
            if(next.Length==0)paragraph.Inlines.Add(new Run(setup?"Complete speech setup to begin.":state.Sources.ContainsKey(Slot)?"Listening for conversation…":"Choose an audio source in Controls."));
            Text.FontSize=size;Text.Document.FontSize=size;paragraph.LineHeight=size*1.4;paragraph.LineStackingStrategy=LineStackingStrategy.BlockLineHeight;
            segments=next;lastSetup=setup;
        }finally{Text.EndChange();}
        if(geometryChanged)QueueRestore(anchor,offset,fontChanged||resetView);
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
    void Trace(string reason){if(!traceEnabled)return;geometry.Enqueue(new{reason,slot=Slot,offset=Text.VerticalOffset,extent=Text.ExtentHeight,viewport=Text.ViewportHeight,width=Text.ViewportWidth,padding=Text.Document.PagePadding.Bottom,count=segments.Length,following=follow.Following,readerNavigating,readerSelecting,revision=follow.Revision});while(geometry.Count>128)geometry.Dequeue();}
    internal object ViewportDiagnostics(){
        Rect? rect=null;try{if(IsLoaded)rect=paragraph.Inlines.OfType<Run>().LastOrDefault()?.ContentEnd.GetCharacterRect(LogicalDirection.Backward);}catch(InvalidOperationException){}
        return new{slot=Slot,following=follow.Following,readerNavigating,readerSelecting,readerStartOffset,thumbDragging,pending,settling,offset=Text.VerticalOffset,extent=Text.ExtentHeight,viewport=Text.ViewportHeight,width=Text.ViewportWidth,padding=Text.Document.PagePadding.Bottom,latestTop=rect is{IsEmpty:false}?rect?.Top:null,latestBottom=rect is{IsEmpty:false}?rect?.Bottom:null,steps=Geometry().TakeLast(10)};
    }
    internal bool LatestVisible(){var rect=paragraph.Inlines.OfType<Run>().LastOrDefault()?.ContentEnd.GetCharacterRect(LogicalDirection.Backward);return rect is {} r&&!r.IsEmpty&&r.Top>=-1&&r.Bottom<=Text.ViewportHeight+1;}
    internal (string Key,int Index,double Y)? ReadingMark(){var a=Capture();return a==null?null:(a.Key,a.Index,a.Y);}
    internal double? MarkY(string key,int index){if(!runs.TryGetValue(key,out var run))return null;var p=run.ContentStart.GetPositionAtOffset(Math.Clamp(index,0,run.Text.Length));var r=p?.GetCharacterRect(LogicalDirection.Forward);return r is {} rect&&!rect.IsEmpty?rect.Top:null;}
    internal object[] Geometry()=>geometry.ToArray();
}
