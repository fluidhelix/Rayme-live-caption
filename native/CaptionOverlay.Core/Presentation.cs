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

// Continuous text is a presentation of the authoritative bounded segment history.
public record Transcript(string FinalText,string PartialText,string Label,bool Live);
public static class TranscriptPresentation {
    public static string Finish(string text) {
        text=text.Trim();if(text.Length==0)return text;
        // Keep annotation bytes intact, including multiple trailing vocal cues.
        var cue=System.Text.RegularExpressions.Regex.Match(text,@"(?:\[[^\]]+\]|\*[^*]+\*)(?:\s*(?:\[[^\]]+\]|\*[^*]+\*))*$");
        if(cue.Success){string speech=text[..cue.Index].TrimEnd();return speech.Length==0?text:Sentence(speech)+" "+cue.Value;}
        return Sentence(text);
    }
    static string Sentence(string text){
        string spoken=text.TrimEnd('"','\'','”','’',')');
        if(spoken.Length==0||char.IsPunctuation(spoken[^1]))return text;
        int end=spoken.Length;return text[..end]+"."+text[end..];
    }
    public static Transcript Build(CaptionState state,string slot) {
        var finals=state.History.Where(c=>c.Source.Id==slot).OrderBy(c=>c.CapturedAt).Select(c=>Finish(c.Text));
        string text=string.Join(" ",finals.Where(t=>t.Length>0));
        state.Partials.TryGetValue(slot,out var partial);
        string live=partial?.Text.Trim()??"";
        string title=state.Sources.GetValueOrDefault(slot)?.Source.Name??state.History.LastOrDefault(c=>c.Source.Id==slot)?.Source.Name??(slot=="device"?"Windows playback":"Choose Application "+slot.ToUpperInvariant());
        return new(text,live,slot=="device"?title:$"Application {slot.ToUpperInvariant()} · {title}",live.Length>0);
    }
    public static string[] Slots(CaptionState state)=>state.Sources.ContainsKey("device")||!state.Sources.ContainsKey("a")&&!state.Sources.ContainsKey("b")&&!state.History.Any(c=>c.Source.Id is "a" or "b")?["device"]:["a","b"];
}
public sealed class ScrollFollow {
    public bool Following { get; private set; }=true;
    public long Revision {get;private set;}
    public void ReaderMoved(double offset,double scrollable){Following=scrollable-offset<=60;Revision++;}
}
public static class WindowInteraction {
    public static string LockGlyph(bool locked)=>locked?"\uE72E":"\uE785";
    public static string LockAction(bool locked)=>locked?"Unlock overlay position":"Lock overlay position";
    public static string LockHint(bool locked)=>locked?"Position and size locked — unlock position":"Position and size unlocked — lock position";
}
