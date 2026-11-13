using System;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Shapes;
namespace Rayme.Overlay;
internal sealed class ColorPicker : ModernWindow {
    readonly Border spectrum=new(){Height=148,CornerRadius=new(6),ClipToBounds=true};
    readonly Canvas plane=new(){Background=Brushes.Transparent};
    readonly Ellipse marker=new(){Width=13,Height=13,Stroke=Brushes.White,StrokeThickness=2,Fill=Brushes.Transparent,IsHitTestVisible=false};
    readonly Slider hue=new(){Minimum=0,Maximum=360,Margin=new(0,12,0,12)};
    readonly TextBox hex=new(){MaxLength=7};
    readonly TextBox[] rgb=[new(),new(),new()];
    readonly TextBlock warning=Theme.Text("",12,"#F2C282"),sampleTitle=Theme.Text("",14,strong:true);
    readonly Border swatch=new(){Width=36,Height=36,CornerRadius=new(6),Margin=new(0,0,10,0)};
    readonly Button apply;
    readonly string defaultColor;
    double h,s,v;
    bool sync;
    internal string Selected {get;private set;}
    internal TextBox HexField=>hex;
    internal Button ApplyButton=>apply;
    internal Button RestoreButton {get;}
    public ColorPicker(string name,string current,string factory):this(new StackPanel{Margin=new(22,0,22,20)},name,current,factory){}
    ColorPicker(StackPanel body,string name,string current,string factory):base(name+" · Title color","Only the source title changes",body){
        Width=392;Height=650;MinWidth=360;MinHeight=550;defaultColor=factory;Selected=current;
        Theme.Accessible(spectrum,"Saturation and brightness; HEX and RGB fields provide keyboard alternatives");
        var layers=new Grid();layers.Children.Add(new Border{Background=new LinearGradientBrush(Colors.White,Colors.Transparent,0)});layers.Children.Add(new Border{Background=new LinearGradientBrush(Colors.Transparent,Colors.Black,90)});layers.Children.Add(plane);plane.Children.Add(marker);spectrum.Child=layers;body.Children.Add(spectrum);
        Theme.Accessible(hue,"Hue");hue.SetResourceReference(StyleProperty,"HueSlider");body.Children.Add(hue);
        var hexRow=new DockPanel();DockPanel.SetDock(swatch,Dock.Left);hexRow.Children.Add(swatch);Theme.Accessible(hex,"Source color HEX");hexRow.Children.Add(hex);body.Children.Add(hexRow);
        var fields=new Grid{Margin=new(0,12,0,12)};
        for(int i=0;i<3;i++){fields.ColumnDefinitions.Add(new());var group=new StackPanel{Margin=new(i==0?0:8,0,0,0)};string label=new[]{"Red","Green","Blue"}[i];group.Children.Add(Theme.Text(label,12,"#B9B9B9"));Theme.Accessible(rgb[i],label);group.Children.Add(rgb[i]);Grid.SetColumn(group,i);fields.Children.Add(group);rgb[i].TextChanged+=(_,_)=>ReadRgb();}body.Children.Add(fields);
        sampleTitle.Text=name;var sample=new StackPanel();sample.Children.Add(sampleTitle);sample.Children.Add(Theme.Text("Every word stays clear and neutral.",16,"#F2F2F2"));body.Children.Add(new Border{Background=Brushes.Black,CornerRadius=new(7),Padding=new(12),Child=sample});body.Children.Add(warning);
        var actions=new DockPanel{Margin=new(0,14,0,0)};
        RestoreButton=Theme.Button("Restore default",(_,_)=>Set(defaultColor));DockPanel.SetDock(RestoreButton,Dock.Left);actions.Children.Add(RestoreButton);
        var cancel=Theme.Button("Cancel",(_,_)=>{DialogResult=false;});cancel.IsCancel=true;DockPanel.SetDock(cancel,Dock.Right);actions.Children.Add(cancel);
        apply=Theme.Button("Apply",(_,_)=>{DialogResult=true;},style:"PrimaryButton");apply.IsDefault=true;DockPanel.SetDock(apply,Dock.Right);actions.Children.Add(apply);body.Children.Add(actions);
        hex.TextChanged+=(_,_)=>{if(sync)return;if(SourceColor.TryHex(hex.Text,out var value))Set(value);else{warning.Text="Use six hex digits, such as #80DFFF.";apply.IsEnabled=false;}};
        hue.ValueChanged+=(_,e)=>{if(sync)return;h=e.NewValue;Set(SourceColor.FromHsv(h,s,v),false);};
        void Point(MouseEventArgs e){var p=e.GetPosition(spectrum);s=Math.Clamp(p.X/Math.Max(1,spectrum.ActualWidth),0,1);v=1-Math.Clamp(p.Y/Math.Max(1,spectrum.ActualHeight),0,1);Set(SourceColor.FromHsv(h,s,v),false);}
        spectrum.MouseLeftButtonDown+=(_,e)=>{spectrum.CaptureMouse();Point(e);e.Handled=true;};spectrum.MouseMove+=(_,e)=>{if(spectrum.IsMouseCaptured&&e.LeftButton==MouseButtonState.Pressed)Point(e);};spectrum.MouseLeftButtonUp+=(_,_)=>spectrum.ReleaseMouseCapture();
        spectrum.SizeChanged+=(_,_)=>PositionMarker();Set(current);
    }
    void ReadRgb(){if(sync)return;byte[] channels=new byte[3];for(int i=0;i<3;i++)if(!byte.TryParse(rgb[i].Text,NumberStyles.None,CultureInfo.InvariantCulture,out channels[i])){warning.Text="Red, Green and Blue must be integers from 0 to 255.";apply.IsEnabled=false;return;}Set($"#{channels[0]:X2}{channels[1]:X2}{channels[2]:X2}");}
    void Set(string value,bool convert=true){
        if(!SourceColor.TryHex(value,out var normalized))return;sync=true;try{
            Selected=normalized;if(convert)(h,s,v)=SourceColor.Hsv(normalized);hex.Text=normalized;var(r,g,b)=SourceColor.Rgb(normalized);rgb[0].Text=r.ToString(CultureInfo.InvariantCulture);rgb[1].Text=g.ToString(CultureInfo.InvariantCulture);rgb[2].Text=b.ToString(CultureInfo.InvariantCulture);hue.Value=h;
            spectrum.Background=Theme.Brush(SourceColor.FromHsv(h,1,1));swatch.Background=sampleTitle.Foreground=Theme.Brush(normalized);PositionMarker();warning.Text=SourceColor.ContrastOnBlack(normalized)<4.5?"This title color may be difficult to read on black.":"";apply.IsEnabled=true;
        }finally{sync=false;}
    }
    void PositionMarker(){Canvas.SetLeft(marker,Math.Clamp(s*spectrum.ActualWidth-6,0,Math.Max(0,spectrum.ActualWidth-13)));Canvas.SetTop(marker,Math.Clamp((1-v)*spectrum.ActualHeight-6,0,Math.Max(0,spectrum.ActualHeight-13)));}
}
