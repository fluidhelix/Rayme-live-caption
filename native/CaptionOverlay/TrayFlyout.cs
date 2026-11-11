using System;
using System.Collections.Generic;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Runtime.InteropServices;

namespace Rayme.Overlay;

internal record TrayAction(string Label,string Glyph,Action Execute);

internal sealed class TrayFlyout : Window {
    [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr window);
    readonly StackPanel items=new();
    readonly List<Button> buttons=[];
    bool dismissing;
    public TrayFlyout(TrayAction[] actions) {
        Title="Rayme tray menu";WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
        Foreground=Theme.Brush("#F4F7FC");FontFamily=new FontFamily("Segoe UI Variable Text, Segoe UI");ShowInTaskbar=false;Topmost=true;ResizeMode=ResizeMode.NoResize;
        Width=286;SizeToContent=SizeToContent.Height;
        KeyboardNavigation.SetTabNavigation(items,KeyboardNavigationMode.Cycle);
        var heading=new DockPanel { Margin=new Thickness(10,8,8,12) };var mark=Theme.Mark(26);mark.Margin=new Thickness(0,0,10,0);heading.Children.Add(mark);var words=new StackPanel();words.Children.Add(Theme.Text("Rayme",15,strong:true));words.Children.Add(Theme.Text("Live captions, within reach",11,"#B9C7DC"));heading.Children.Add(words);items.Children.Add(heading);
        for(int i=0;i<actions.Length;i++) {
            var action=actions[i];
            if(i==actions.Length-1)items.Children.Add(new Border { Height=1,Background=Theme.Brush("#3D4D63"),Margin=new Thickness(8,6,8,6) });
            var b=Theme.Button(action.Label,(_,_)=>{Close();action.Execute();},action.Glyph);
            b.HorizontalContentAlignment=HorizontalAlignment.Left;b.Margin=new Thickness(0,2,0,2);b.BorderBrush=Brushes.Transparent;b.Background=Brushes.Transparent;
            // The shared button template aligns arbitrary content using this property.
            if(b.Content is StackPanel row)row.HorizontalAlignment=HorizontalAlignment.Left;
            items.Children.Add(b);buttons.Add(b);
        }
        Content=new Border { Background=Theme.Brush("#182231"),CornerRadius=new CornerRadius(14),BorderBrush=Theme.Brush("#60718B"),BorderThickness=new Thickness(1),Padding=new Thickness(8),Child=items };
        Closing+=(_,_)=>dismissing=true;
        Deactivated+=(_,_)=>{if(!dismissing)Close();};
        PreviewKeyDown+=(_,e)=>{
            if(e.Key==Key.Escape){e.Handled=true;Close();return;}
            int index=buttons.FindIndex(b=>b.IsKeyboardFocusWithin);
            if(e.Key==Key.Down||e.Key==Key.Up||e.Key==Key.Home||e.Key==Key.End) {
                int next=e.Key==Key.Home?0:e.Key==Key.End?buttons.Count-1:e.Key==Key.Down?(index+1)%buttons.Count:(index+buttons.Count-1)%buttons.Count;
                buttons[next].Focus();e.Handled=true;
            }
        };
    }
    public void ShowAt(Bounds anchor) {
        Show();UpdateLayout();
        var displays=DesktopPlacement.Displays();
        var display=DesktopPlacement.DisplayFor(anchor,displays);
        var hwnd=new WindowInteropHelper(this).Handle;
        // Establish destination DPI, then measure there before computing the final physical bounds.
        DesktopPlacement.Apply(hwnd,FlyoutPlacement.Place(anchor,ActualWidth*display.Scale,ActualHeight*display.Scale,display));
        UpdateLayout();DesktopPlacement.Apply(hwnd,FlyoutPlacement.Place(anchor,ActualWidth*display.Scale,ActualHeight*display.Scale,display));
        SetForegroundWindow(hwnd);Activate();buttons[0].Focus();
    }
    internal int ActionCount=>buttons.Count;
    internal Button FirstAction=>buttons[0];
    internal string? FocusedAction=>buttons.FirstOrDefault(b=>b.IsKeyboardFocusWithin)?.ToolTip as string;
}
