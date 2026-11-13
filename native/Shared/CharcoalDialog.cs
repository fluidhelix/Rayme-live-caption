using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
namespace Rayme;
// Rayme-owned prompts use the same charcoal resource dictionary. No undocumented
// Windows theme hooks; native OS file/loader dialogs continue to follow Windows.
internal static class CharcoalDialog {
    internal static int Show(string text,string title,uint flags){
        System.Windows.Media.RenderOptions.ProcessRenderMode=System.Windows.Interop.RenderMode.SoftwareOnly;
        if(Application.Current==null)_=new Application{ShutdownMode=ShutdownMode.OnExplicitShutdown};
        var assembly=typeof(CharcoalDialog).Assembly.GetName().Name;
        var window=new Window{Title=title,Width=510,SizeToContent=SizeToContent.Height,MinWidth=350,MaxHeight=650,WindowStartupLocation=WindowStartupLocation.CenterScreen,WindowStyle=WindowStyle.None,ResizeMode=ResizeMode.NoResize,AllowsTransparency=true,Background=Brushes.Transparent,Foreground=new SolidColorBrush(Color.FromRgb(242,242,242)),FontFamily=new FontFamily("Segoe UI"),Icon=new BitmapImage(new Uri($"pack://application:,,,/{assembly};component/Assets/Rayme.ico"))};
        window.Resources.MergedDictionaries.Add(new ResourceDictionary{Source=new Uri($"pack://application:,,,/{assembly};component/Themes/Rayme.xaml")});
        int result=2;var panel=new StackPanel{Margin=new Thickness(22)};
        var header=new DockPanel{Margin=new Thickness(0,0,0,16)};var name=new TextBlock{Text=title,FontSize=18,FontWeight=FontWeights.SemiBold,TextWrapping=TextWrapping.Wrap};header.Children.Add(name);header.MouseLeftButtonDown+=(_,e)=>{if(e.LeftButton==MouseButtonState.Pressed)window.DragMove();};panel.Children.Add(header);
        panel.Children.Add(new ScrollViewer{MaxHeight=430,VerticalScrollBarVisibility=ScrollBarVisibility.Auto,HorizontalScrollBarVisibility=ScrollBarVisibility.Disabled,Content=new TextBlock{Text=text,TextWrapping=TextWrapping.Wrap,FontSize=14,Margin=new Thickness(0,0,0,20)}});
        var actions=new StackPanel{Orientation=Orientation.Horizontal,HorizontalAlignment=HorizontalAlignment.Right};
        void Add(string label,int code,bool cancel){var b=new Button{Content=label,IsDefault=!cancel,IsCancel=cancel,Margin=new Thickness(8,0,0,0)};System.Windows.Automation.AutomationProperties.SetName(b,label);b.Click+=(_,_)=>{result=code;window.Close();};actions.Children.Add(b);}
        if((flags&0xF)==5){Add("Retry",4,false);Add("Cancel",2,true);}else Add("OK",1,false);
        panel.Children.Add(actions);window.Content=new Border{Background=new SolidColorBrush(Color.FromRgb(20,20,20)),BorderBrush=new SolidColorBrush(Color.FromRgb(80,80,80)),BorderThickness=new Thickness(1),CornerRadius=new CornerRadius(13),Child=panel};
        window.PreviewKeyDown+=(_,e)=>{if(e.Key==Key.Escape){result=2;window.Close();e.Handled=true;}};
        window.ShowDialog();return result;
    }
}
