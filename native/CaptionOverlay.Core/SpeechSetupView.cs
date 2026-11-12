using System.Text.Json;
namespace Rayme.Overlay;

public record SpeechSetupView(string Phase,string Message,string ActiveModel,string SelectedModel,bool Required) {
    public bool Busy=>Phase is "checking" or "downloading" or "verifying" or "loading" or "switching";
    public bool Ready=>Phase=="ready"&&!Required&&ActiveModel.Length>0;
    public static SpeechSetupView Read(JsonElement? value){
        if(value is not {} data)return new("checking","Checking local speech setup…","","",true);
        string Text(string name)=>data.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()??"":"";
        string Model(string name)=>data.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.Object&&v.TryGetProperty("name",out var n)?n.GetString()??"":"";
        return new(Text("phase"),Text("message"),Model("activeModel"),Model("selectedModel"),!data.TryGetProperty("required",out var r)||r.ValueKind!=JsonValueKind.False);
    }
}
