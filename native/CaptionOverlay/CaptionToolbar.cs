using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Shell;
namespace Rayme.Overlay;
internal enum PairingMode { None,Attach,Detach,Choose }
internal sealed class CaptionToolbar : DockPanel {
    internal readonly TextBlock Status=new(){FontSize=12,Foreground=Theme.Brush("#C4C4C4"),TextWrapping=TextWrapping.NoWrap,TextTrimming=TextTrimming.CharacterEllipsis,VerticalAlignment=VerticalAlignment.Center};
    readonly Border dot=new(){Width=7,Height=7,CornerRadius=new(4),Background=Theme.Brush("#70BD5B"),Margin=new(0,0,8,0),VerticalAlignment=VerticalAlignment.Center};
    PairingMode currentMode=(PairingMode)(-1);
    (string Status,string Detail,bool Locked,PairingMode Mode,bool Error,bool Paused)? shown;
    internal Button Lock {get;}
    internal Button Settings {get;}
    internal Button Layout {get;}
    internal CaptionToolbar(Action hide,Action layout,Action locking,Action settings){
        Height=36;LastChildFill=true;Margin=new(12,0,8,0);WindowChrome.SetIsHitTestVisibleInChrome(this,true);
        var actions=new StackPanel{Orientation=Orientation.Horizontal,VerticalAlignment=VerticalAlignment.Center};SetDock(actions,Dock.Right);Children.Add(actions);
        Button Icon(string glyph,string name,Action callback){var b=Theme.IconButton(glyph,name,(_,_)=>callback());b.Width=28;b.Height=28;b.MinHeight=28;b.FontSize=16;actions.Children.Add(b);return b;}
        Icon("\uE738","Hide overlay",hide);
        Layout=Icon("","Attach windows",layout);
        Lock=Icon("\uE785","Lock overlay position",locking);Settings=Icon("\uE713","Open Controls",settings);
        SetDock(dot,Dock.Left);Children.Add(dot);Children.Add(Status);
    }
    static FrameworkElement PairingIcon(PairingMode mode){
        string data=mode switch{
            PairingMode.Attach=>"M 10 13 A 5 5 0 0 0 17 13.5 L 20 10.5 A 5 5 0 0 0 13 3.5 L 11.2 5.3 M 14 11 A 5 5 0 0 0 7 10.5 L 4 13.5 A 5 5 0 0 0 11 20.5 L 12.8 18.7",
            PairingMode.Detach=>"M 8 3 L 8 6 M 3 8 L 6 8 M 16 21 L 16 18 M 21 16 L 18 16 M 8 16 L 7 17 A 4 4 0 0 1 1 11 L 5 7 M 16 8 L 17 7 A 4 4 0 0 1 23 13 L 19 17",
            _=>"M 3 3 L 21 3 L 21 21 L 3 21 Z M 3 9 L 21 9 M 10 9 L 10 21"};
        return new Viewbox{Width=18,Height=18,Child=new System.Windows.Shapes.Path{Width=24,Height=24,Data=Geometry.Parse(data),Stroke=Theme.Brush("#E8E8E8"),StrokeThickness=1.8,StrokeStartLineCap=PenLineCap.Round,StrokeEndLineCap=PenLineCap.Round,StrokeLineJoin=PenLineJoin.Round}};
    }
    internal void Update(string status,string detail,bool locked,PairingMode mode,bool error,bool paused){
        var next=(status,detail,locked,mode,error,paused);if(shown==next)return;shown=next;
        Cursor=locked?System.Windows.Input.Cursors.Arrow:System.Windows.Input.Cursors.SizeAll;Status.Text=status;Status.ToolTip=detail;Theme.Accessible(Status,detail);dot.Background=Theme.Brush(error?"#F2C282":paused?"#A0A0A0":"#70BD5B");
        Lock.Content=WindowInteraction.LockGlyph(locked);Theme.Accessible(Lock,WindowInteraction.LockAction(locked));Lock.ToolTip=WindowInteraction.LockHint(locked);
        string label=mode switch{PairingMode.Detach=>"Detach windows",PairingMode.Choose=>"Choose application to attach",_=>"Attach windows"};
        if(currentMode!=mode){Layout.Content=PairingIcon(mode);currentMode=mode;}Layout.IsEnabled=mode!=PairingMode.None;Layout.Visibility=mode==PairingMode.None?Visibility.Collapsed:Visibility.Visible;
        Theme.Accessible(Layout,label);Layout.ToolTip=label;
    }
}
