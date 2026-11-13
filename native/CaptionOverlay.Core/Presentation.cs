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
public record Transcript(string FinalText,string PartialText,string Label,bool Live,bool Unfinished=false);
public record TranscriptHeader(string Label,bool Live,bool Unfinished);
public static class TranscriptPresentation {
    public static TranscriptHeader Header(CaptionState state,string slot)=>new(
        state.Sources.GetValueOrDefault(slot)?.Source.Name??state.History.LastOrDefault(c=>c.Source.Id==slot)?.Source.Name??(slot=="device"?"Windows playback":"Choose Application "+slot.ToUpperInvariant()),
        state.Partials.TryGetValue(slot,out var partial)&&!string.IsNullOrWhiteSpace(partial.Text),state.History.Any(c=>c.Source.Id==slot&&c.Unfinished));
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
        var finals=state.History.Where(c=>c.Source.Id==slot&&c.Final).OrderBy(c=>c.CapturedAt).Select(c=>Finish(c.Text));
        string text=string.Join(" ",finals.Where(t=>t.Length>0));
        state.Partials.TryGetValue(slot,out var partial);
        string live=partial?.Text.Trim()??"";
        var header=Header(state,slot);return new(text,live,header.Label,header.Live,header.Unfinished);
    }
    public static string[] Slots(CaptionState state)=>state.Sources.ContainsKey("device")?["device"]:new[]{"a","b"}.Where(state.Sources.ContainsKey).ToArray();
    public static TranscriptSegment[] Segments(CaptionState state,string slot){
        var list=state.History.Where(c=>c.Source.Id==slot).OrderBy(c=>c.CapturedAt).Select(c=>new TranscriptSegment(CaptionPresentation.RowKey(c),c.Unfinished?c.Text:Finish(c.Text),c.Final,c.Unfinished)).ToList();
        if(state.Partials.TryGetValue(slot,out var p)&&p.Text.Trim().Length>0)list.Add(new(CaptionPresentation.RowKey(p),p.Text.Trim(),false));
        return list.ToArray();
    }

}
public sealed class ScrollFollow {
    public bool Following { get; private set; }=true;
    public long Revision {get;private set;}
    public void ReaderMoved(double offset,double scrollable){Following=scrollable-offset<=60;Revision++;}
    public void BeginNavigation(){Following=false;Revision++;}
    public bool NeedsReveal(double top,double bottom,double viewport)=>Following&&double.IsFinite(top)&&double.IsFinite(bottom)&&viewport>0&&(bottom>viewport+.5||top<-.5);
}
public static class WindowInteraction {
    public static string LockGlyph(bool locked)=>locked?"\uE72E":"\uE785";
    public static string LockAction(bool locked)=>locked?"Unlock overlay position":"Lock overlay position";
    public static string LockHint(bool locked)=>locked?"Position and size locked — unlock position":"Position and size unlocked — lock position";
}

public record TranscriptSegment(string Key,string Text,bool Final,bool Unfinished=false);
public sealed class CaptionLayout {
    public bool Detached {get;private set;}
    public static bool CanDetach(CaptionState state)=>TranscriptPresentation.Slots(state).SequenceEqual(new[]{"a","b"});
    public bool Detach(CaptionState state){if(!CanDetach(state))return false;Detached=true;return true;}
    public void Reattach()=>Detached=false;
    public void Synchronize(CaptionState state){if(!CanDetach(state))Detached=false;}
}
public static class TextPosition {
    // Preserve positions in unchanged prefixes/suffixes; corrected words map to
    // the nearest surviving boundary. No obsolete text is retained to hold layout.
    public static int Map(string oldText,string newText,int offset){
        offset=Math.Clamp(offset,0,oldText.Length);int prefix=0,suffix=0;
        while(prefix<Math.Min(oldText.Length,newText.Length)&&oldText[prefix]==newText[prefix])prefix++;
        while(suffix<Math.Min(oldText.Length,newText.Length)-prefix&&oldText[^(suffix+1)]==newText[^(suffix+1)])suffix++;
        if(offset<=prefix)return Math.Min(offset,newText.Length);
        if(offset>=oldText.Length-suffix)return Math.Clamp(newText.Length-(oldText.Length-offset),0,newText.Length);
        return offset-prefix<=oldText.Length-suffix-offset?prefix:newText.Length-suffix;
    }
}
