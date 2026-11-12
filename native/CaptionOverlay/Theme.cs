using System;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Linq;

namespace Rayme.Overlay;

internal static class Theme {
    public static readonly Uri IconUri = new("pack://application:,,,/CaptionOverlay;component/Assets/Rayme.ico");
    public static SolidColorBrush Brush(string hex) { var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); b.Freeze(); return b; }
    public static void Install(Application app) => app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri("/CaptionOverlay;component/Themes/Rayme.xaml",UriKind.Relative) });
    public static TextBlock Text(string text, double size=14, string color="#F4F7FC", bool strong=false) => new() { Text=text,FontSize=size,Foreground=Brush(color),FontWeight=strong?FontWeights.SemiBold:FontWeights.Normal,TextWrapping=TextWrapping.Wrap };
    public static TextBlock Glyph(string glyph, double size=16) => new() { Text=glyph,FontSize=size,FontFamily=new FontFamily("Segoe Fluent Icons, Segoe MDL2 Assets"),VerticalAlignment=VerticalAlignment.Center };
    public static Button Button(string label, RoutedEventHandler action, string? glyph=null, string? style=null) {
        var button=new Button { Margin=new Thickness(0,0,8,8),ToolTip=label };
        AutomationProperties.SetName(button,label);
        System.Windows.Shell.WindowChrome.SetIsHitTestVisibleInChrome(button,true);
        if (style != null) button.SetResourceReference(FrameworkElement.StyleProperty,style);
        if (glyph == null) button.Content=label;
        else { var row=new StackPanel { Orientation=Orientation.Horizontal };var icon=Glyph(glyph,14);icon.Margin=new Thickness(0,0,8,0);row.Children.Add(icon);row.Children.Add(Text(label,13));button.Content=row; }
        button.Click+=action;return button;
    }
    public static Button IconButton(string glyph,string label,RoutedEventHandler action) {
        var b=Button(label,action,style:"IconButton");b.Content=glyph;b.Margin=new Thickness(2,0,0,0);return b;
    }
    static ImageSource? mark;
    public static Image Mark(double size=24) => new() { Source=mark??=new IconBitmapDecoder(IconUri,BitmapCreateOptions.PreservePixelFormat,BitmapCacheOption.OnLoad).Frames.OrderByDescending(f=>f.PixelWidth).First(),Width=size,Height=size,VerticalAlignment=VerticalAlignment.Center };
    public static Border Card(UIElement content, Thickness? padding=null) => new() { Background=Brush("#1B2432"),BorderBrush=Brush("#344358"),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(12),Padding=padding??new Thickness(16),Margin=new Thickness(0,0,0,10),Child=content };
    public static void Accessible(DependencyObject control,string name) => AutomationProperties.SetName(control,name);
}
