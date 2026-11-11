namespace Rayme.Overlay;

// Visual identity deliberately survives partial -> final; protocol deduplication keys stay unchanged.
public static class CaptionPresentation {
    public static string RowKey(Caption caption) => $"{caption.Source.Id}:{caption.Source.Generation}:{caption.Id}";
}

public static class FlyoutPlacement {
    public static Bounds Place(Bounds icon,double width,double height,Display display) {
        var w=display.Work;double gap=8*display.Scale;
        width=Math.Min(width,w.Width);height=Math.Min(height,w.Height);
        double x=icon.X+icon.Width-width, y=icon.Y-height-gap;
        if (icon.Y < w.Y+w.Height/2) y=icon.Y+icon.Height+gap;
        if (icon.X < w.X) x=icon.X+icon.Width+gap;
        else if (icon.X+icon.Width > w.X+w.Width) x=icon.X-width-gap;
        return new(Math.Clamp(x,w.X,w.X+w.Width-width),Math.Clamp(y,w.Y,w.Y+w.Height-height),width,height);
    }
}
