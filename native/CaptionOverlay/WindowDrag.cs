using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Media;
namespace Rayme.Overlay;
internal static class WindowDrag {
    internal static T? Ancestor<T>(DependencyObject? value) where T:DependencyObject {
        while(value!=null){if(value is T found)return found;value=value is FrameworkContentElement c?c.Parent:value is Visual||value is System.Windows.Media.Media3D.Visual3D?VisualTreeHelper.GetParent(value):LogicalTreeHelper.GetParent(value);}return null;
    }
    internal static void Install(Window window,Func<bool> locked){
        Point? start=null;
        window.PreviewMouseLeftButtonDown+=(_,e)=>{
            if(locked()||Keyboard.Modifiers!=ModifierKeys.None)return;
            var source=e.OriginalSource as DependencyObject;
            if(Ancestor<ButtonBase>(source)!=null||Ancestor<ScrollBar>(source)!=null||Ancestor<Thumb>(source)!=null||Ancestor<ResizeGrip>(source)!=null||Ancestor<Slider>(source)!=null||Ancestor<ComboBox>(source)!=null)return;
            if(Ancestor<TextBoxBase>(source) is {} editor){if(editor is not RichTextBox rich||rich.Tag is not TranscriptPanel panel||panel.HasTextAt(e.GetPosition(rich)))return;e.Handled=true;}
            start=e.GetPosition(window);
        };
        window.PreviewMouseMove+=(_,e)=>{if(start is not {} origin||e.LeftButton!=MouseButtonState.Pressed||locked())return;var point=e.GetPosition(window);if(Math.Abs(point.X-origin.X)<SystemParameters.MinimumHorizontalDragDistance&&Math.Abs(point.Y-origin.Y)<SystemParameters.MinimumVerticalDragDistance)return;start=null;try{window.DragMove();e.Handled=true;}catch(InvalidOperationException){}};
        window.PreviewMouseLeftButtonUp+=(_,_)=>start=null;
    }
}
