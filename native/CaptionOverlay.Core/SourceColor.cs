using System.Globalization;
namespace Rayme.Overlay;
public static class SourceColor {
    public static string Default(string slot)=>slot=="a"?"#80DFFF":slot=="b"?"#FFD68A":"#CBB9FF";
    public static bool TryHex(string? value,out string normalized){normalized="";if(value?.Length!=7||value[0]!='#'||!uint.TryParse(value.AsSpan(1),NumberStyles.HexNumber,CultureInfo.InvariantCulture,out _))return false;normalized=value.ToUpperInvariant();return true;}
    public static (byte R,byte G,byte B) Rgb(string hex){uint n=uint.Parse(hex.AsSpan(1),NumberStyles.HexNumber,CultureInfo.InvariantCulture);return((byte)(n>>16),(byte)(n>>8),(byte)n);}
    public static (double H,double S,double V) Hsv(string hex){var(r,g,b)=Rgb(hex);double R=r/255d,G=g/255d,B=b/255d,max=Math.Max(R,Math.Max(G,B)),min=Math.Min(R,Math.Min(G,B)),d=max-min,h=d==0?0:max==R?60*((G-B)/d%6):max==G?60*((B-R)/d+2):60*((R-G)/d+4);return((h+360)%360,max==0?0:d/max,max);}
    public static string FromHsv(double hue,double saturation,double value){double h=double.IsFinite(hue)?(hue%360+360)%360:0,s=double.IsFinite(saturation)?Math.Clamp(saturation,0,1):0,v=double.IsFinite(value)?Math.Clamp(value,0,1):0,c=v*s,x=c*(1-Math.Abs(h/60%2-1)),m=v-c;var(r,g,b)=h<60?(c,x,0d):h<120?(x,c,0d):h<180?(0d,c,x):h<240?(0d,x,c):h<300?(x,0d,c):(c,0d,x);return $"#{(int)Math.Round((r+m)*255):X2}{(int)Math.Round((g+m)*255):X2}{(int)Math.Round((b+m)*255):X2}";}
    public static double ContrastOnBlack(string hex){var(r,g,b)=Rgb(hex);double Linear(byte n){double s=n/255d;return s<=.04045?s/12.92:Math.Pow((s+.055)/1.055,2.4);}return(.2126*Linear(r)+.7152*Linear(g)+.0722*Linear(b)+.05)/.05;}
}
