namespace Rayme.Overlay;

// Mirrors history-policy.js. Count, UTF-16 and conservative encoded-byte limits
// apply independently to each source, including retained unfinished recognition.
public static class HistoryPolicy {
    public const int Segments=200,Characters=65536,Bytes=131072,TextLimit=4000;
    public static string Limit(string text){int end=Math.Min(text.Length,TextLimit);if(end<text.Length&&char.IsHighSurrogate(text[end-1])&&char.IsLowSurrogate(text[end]))end--;return text[..end];}
    static int EncodedBytes(string text){int bytes=0;foreach(char c in text)bytes+=c<32||char.IsSurrogate(c)?6:c=='"'||c=='\\'?2:c<128?1:c<2048?2:3;return bytes;}
    public static int Cost(Caption c)=>256+EncodedBytes(c.Text)+EncodedBytes(c.Source.Name)+EncodedBytes(c.Source.Identity);
    public static Caption[] Trim(IEnumerable<Caption> history){
        var budgets=new Dictionary<string,(int Count,int Chars,int Bytes,bool Full)>();var kept=new List<Caption>();
        foreach(var c in history.OrderBy(c=>c.CapturedAt).Reverse()){
            var b=budgets.GetValueOrDefault(c.Source.Id);int size=Cost(c);
            if(b.Full||b.Count>=Segments||b.Chars+c.Text.Length>Characters||b.Bytes+size>Bytes){budgets[c.Source.Id]=b with{Full=true};continue;}
            budgets[c.Source.Id]=(b.Count+1,b.Chars+c.Text.Length,b.Bytes+size,false);kept.Add(c);
        }kept.Reverse();return kept.ToArray();
    }
}
