using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rayme.Launcher;

internal sealed class PackageFailure(string reason,string? entry=null,Exception? inner=null):IOException(reason,inner){
    public string Reason { get; }=reason;
    public string? Entry { get; }=entry;
}
internal static class StartupChecks {
    internal static bool Supported(bool windows,Version version,Architecture architecture)=>windows&&version>=new Version(10,0,22000)&&architecture==Architecture.X64;
    internal static string? SafeEntry(string? entry)=>entry==null?null:Regex.IsMatch(entry,@"\A[A-Za-z0-9._/-]{1,200}\z")?entry:"[invalid entry]";
    internal static object Failure(string stage,Exception error)=>new{
        type="launcher-failure",stage,exception=(error.InnerException??error).GetType().FullName,hresult=$"0x{(error.InnerException??error).HResult:X8}",
        nativeErrorCode=error is System.ComponentModel.Win32Exception native?(int?)native.NativeErrorCode:null,
        reason=error is PackageFailure package?package.Reason:error switch{
            FileNotFoundException=>"file-not-found",DirectoryNotFoundException=>"directory-not-found",
            UnauthorizedAccessException=>"access-denied",JsonException=>"invalid-json",IOException=>"io-error",_=>"startup-exception"},
        entry=error is PackageFailure detail?SafeEntry(detail.Entry):null
    };
    internal static void Verify(string root,Action<string>? progress=null){
        var file=Path.Combine(root,"INTEGRITY.json");
        if(new FileInfo(file).Length>1024*1024)throw new PackageFailure("manifest-too-large");
        var entries=JsonSerializer.Deserialize<Dictionary<string,string>>(File.ReadAllText(file))??throw new PackageFailure("invalid-manifest");
        string prefix=Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar)+Path.DirectorySeparatorChar;
        foreach(var (name,expected) in entries){
            string full=Path.GetFullPath(Path.Combine(root,name.Replace('/',Path.DirectorySeparatorChar)));
            if(!full.StartsWith(prefix,StringComparison.OrdinalIgnoreCase)||!Regex.IsMatch(expected,@"\A[0-9a-fA-F]{64}\z"))throw new PackageFailure("invalid-entry",name);
            progress?.Invoke(name);
            try{
                using var content=File.OpenRead(full);
                if(!Convert.ToHexString(SHA256.HashData(content)).Equals(expected,StringComparison.OrdinalIgnoreCase))throw new PackageFailure("integrity-mismatch",name);
            }catch(PackageFailure){throw;}catch(FileNotFoundException e){throw new PackageFailure("file-not-found",name,e);}catch(IOException e){throw new PackageFailure("unreadable-file",name,e);}catch(UnauthorizedAccessException e){throw new PackageFailure("access-denied",name,e);}
        }
        foreach(string candidate in Directory.EnumerateFiles(root,"*",SearchOption.AllDirectories)){
            string relative=Path.GetRelativePath(root,candidate).Replace('\\','/');
            if(relative!="INTEGRITY.json"&&!entries.ContainsKey(relative))throw new PackageFailure("unexpected-file",relative);
        }
    }
}
