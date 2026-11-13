using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
namespace Rayme.Overlay;
internal sealed class CaptionToolbar : DockPanel {
    internal readonly TextBlock Status=new(){FontSize=12,Foreground=Theme.Brush("#C4C4C4"),TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
    readonly Border dot=new(){Width=7,Height=7,CornerRadius=new(4),Background=Theme.Brush("#70BD5B"),Margin=new(0,0,8,0),VerticalAlignment=VerticalAlignment.Center};
    internal Button Lock {get;}
    internal Button Settings {get;}
    internal Button Layout {get;}
    internal CaptionToolbar(Action hide,Action layout,Action locking,Action settings){
        Height=36;LastChildFill=true;Margin=new(12,0,8,0);WindowChrome.SetIsHitTestVisibleInChrome(this,true);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};SetDock(actions,Dock.Right);Children.Add(actions);
        Button Icon(string glyph,string name,Action callback){var b=Theme.IconButton(glyph,name,(_,_)=>callback());b.Width=28;b.Height=28;b.MinHeight=28;b.FontSize=16;actions.Children.Add(b);return b;}
        Icon("\uE738","Hide overlay",hide);
        Layout=Theme.Button("Detach",(_,_)=>layout(),"\uE8A7");Layout.Margin=new(2,0,2,0);Layout.Padding=new(4,2,4,2);Layout.MinHeight=26;Layout.Height=26;Layout.Background=Brushes.Transparent;Layout.BorderBrush=Brushes.Transparent;Layout.FontSize=11;actions.Children.Add(Layout);
        Lock=Icon("\uE785","Lock overlay position",locking);Settings=Icon("\uE713","Open Controls",settings);
        SetDock(dot,Dock.Left);Children.Add(dot);Children.Add(Status);
    }
    internal void Update(string status,string detail,bool locked,bool detached,bool canDetach,bool error,bool paused){
        Cursor=locked?System.Windows.Input.Cursors.Arrow:System.Windows.Input.Cursors.SizeAll;Status.Text=status;Status.ToolTip=detail;Theme.Accessible(Status,detail);dot.Background=Theme.Brush(error?"#F2C282":paused?"#A0A0A0":"#70BD5B");
        Lock.Content=WindowInteraction.LockGlyph(locked);Theme.Accessible(Lock,WindowInteraction.LockAction(locked));Lock.ToolTip=WindowInteraction.LockHint(locked);
        string label=detached?"Reattach":"Detach";Layout.Content=label;Layout.IsEnabled=detached||canDetach;Layout.Name=detached?"ReattachCaptions":"DetachCaptions";
        Theme.Accessible(Layout,label+" captions");Layout.ToolTip=detached?"Return both sources to the combined window":canDetach?"Move both sources into separate caption windows":"Enable both applications to detach";
    }
}
