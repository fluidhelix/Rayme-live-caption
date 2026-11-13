using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shell;

namespace Rayme.Overlay;

internal class ModernWindow : Window {
    public ModernWindow(string title,string subtitle,UIElement body) {
        Title="Rayme · "+title;WindowStyle=WindowStyle.None;AllowsTransparency=true;Background=Brushes.Transparent;
        Foreground=Theme.Brush("#F2F2F2");FontFamily=new FontFamily("Segoe UI Variable Text, Segoe UI");FontSize=14;
        Icon=new BitmapImage(Theme.IconUri);ResizeMode=ResizeMode.CanResizeWithGrip;WindowStartupLocation=WindowStartupLocation.CenterOwner;ShowInTaskbar=false;
        WindowChrome.SetWindowChrome(this,new WindowChrome { CaptionHeight=0,ResizeBorderThickness=new Thickness(7),CornerRadius=new CornerRadius(16),GlassFrameThickness=new Thickness(0),UseAeroCaptionButtons=false });
        var shell=new Grid();shell.RowDefinitions.Add(new(){Height=GridLength.Auto});shell.RowDefinitions.Add(new());
        var header=new DockPanel { Margin=new Thickness(22,18,18,14),LastChildFill=true };
        var close=Theme.IconButton("\uE8BB","Close "+title,(_,_)=>Close());DockPanel.SetDock(close,Dock.Right);header.Children.Add(close);
        var mark=Theme.Mark(34);mark.Margin=new Thickness(0,0,12,0);DockPanel.SetDock(mark,Dock.Left);header.Children.Add(mark);
        var words=new StackPanel();words.Children.Add(Theme.Text(title,20,strong:true));words.Children.Add(Theme.Text(subtitle,12,"#B9B9B9"));header.Children.Add(words);
        header.MouseLeftButtonDown+=(_,e)=>{if(!e.Handled&&e.OriginalSource is not Button&&e.ButtonState==MouseButtonState.Pressed){try{DragMove();}catch(InvalidOperationException){}}};
        shell.Children.Add(header);var scroll=new ScrollViewer { Content=body,Focusable=false,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Margin=new Thickness(0,0,8,10) };Grid.SetRow(scroll,1);shell.Children.Add(scroll);
        Content=new Border { Background=Theme.Brush("#141414"),CornerRadius=new CornerRadius(16),BorderBrush=Theme.Brush("#555555"),BorderThickness=new Thickness(1),Child=shell };
        PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){var dropdown=OpenDropdown(this);if(dropdown!=null)dropdown.IsDropDownOpen=false;else Close();e.Handled=true;}};
        Loaded+=(_,_)=>{var hwnd=new WindowInteropHelper(this).Handle;var displays=DesktopPlacement.Displays();if(displays.Count>0)DesktopPlacement.Apply(hwnd,Preferences.Recover(DesktopPlacement.Read(hwnd),displays));};
    }
    static ComboBox? OpenDropdown(DependencyObject root) {
        if(root is ComboBox combo&&combo.IsDropDownOpen)return combo;
        foreach(var child in LogicalTreeHelper.GetChildren(root))if(child is DependencyObject node&&OpenDropdown(node) is {} open)return open;
        return null;
    }
}
