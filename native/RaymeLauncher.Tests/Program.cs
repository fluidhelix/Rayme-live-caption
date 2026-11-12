using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using Rayme.Launcher;
using Rayme.Testing;

if(args.Contains("--emit")){Console.Write(new string('o',50000)+" http://127.0.0.1/?token="+new string('a',48));Console.Error.Write(new string('e',50000));return 7;}
if(args.Contains("--hang")){Thread.Sleep(10000);return 0;}
int checks=0;
void Check(bool value,string why){if(!value)throw new Exception(why);checks++;}
Check(!StartupChecks.Supported(true,new Version(10,0,20348),Architecture.X64),"Server 2022 must fail the existing Windows build requirement");
Check(StartupChecks.Supported(true,new Version(10,0,22000),Architecture.X64),"Windows 11 minimum is supported");
Check(StartupChecks.Supported(true,new Version(10,0,26100),Architecture.X64),"Server 2025 hosted acceptance is API/build compatible");
Check(!StartupChecks.Supported(false,new Version(10,0,26100),Architecture.X64),"Non-Windows is rejected");
Check(!StartupChecks.Supported(true,new Version(10,0,26100),Architecture.Arm64),"ARM remains unsupported");
string root=Path.Combine(Path.GetTempPath(),"Rayme launcher é 日本 "+Guid.NewGuid());Directory.CreateDirectory(root);
try{
    var file=Path.Combine(root,"Rayme.exe");File.WriteAllText(file,"integrity fixture");
    string manifest=JsonSerializer.Serialize(new Dictionary<string,string>{{"Rayme.exe",Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(file)))}});
    File.WriteAllText(Path.Combine(root,"INTEGRITY.json"),manifest);StartupChecks.Verify(root);checks++;
    File.AppendAllText(file,"corrupt");
    try{StartupChecks.Verify(root);throw new Exception("Corrupt executable accepted");}catch(PackageFailure e){Check(e.Reason=="integrity-mismatch"&&e.Entry=="Rayme.exe","Mismatch identifies the relative entry");}
    File.WriteAllText(file,"integrity fixture");File.WriteAllText(Path.Combine(root,"extra.env"),"private");
    try{StartupChecks.Verify(root);throw new Exception("Unexpected file accepted");}catch(PackageFailure e){Check(e.Reason=="unexpected-file","Unlisted files remain forbidden");}
    File.Delete(Path.Combine(root,"extra.env"));File.Delete(file);
    try{StartupChecks.Verify(root);throw new Exception("Missing file accepted");}catch(PackageFailure e){Check(e.Reason=="file-not-found","Missing file is distinguished from a hash mismatch");}
    File.WriteAllText(Path.Combine(root,"INTEGRITY.json"),JsonSerializer.Serialize(new Dictionary<string,string>{{"../outside",new string('0',64)}}));
    try{StartupChecks.Verify(root);throw new Exception("Traversal accepted");}catch(PackageFailure e){Check(e.Reason=="invalid-entry","Path escape remains forbidden");}
    var safe=JsonSerializer.Serialize(StartupChecks.Failure("test",new InvalidOperationException("SECRET token and transcript")));
    Check(!safe.Contains("SECRET")&&!safe.Contains("transcript")&&safe.Contains("InvalidOperationException"),"Exception type/HResult retained without raw message data");
    Check(StartupChecks.SafeEntry("C:\\Users\\private\\token") == "[invalid entry]","Absolute paths cannot enter package diagnostics");
    Process Spawn(string argument)=>Process.Start(new ProcessStartInfo(Environment.ProcessPath!,argument){UseShellExecute=false,RedirectStandardOutput=true,RedirectStandardError=true})!;
    using(var capture=new DiagnosticCapture(Spawn("--emit"),Path.Combine(root,"capture.json"))){
        Check(capture.Process.WaitForExit(5000),"Both output pipes drain without blocking");capture.Flush();capture.Save();
        using var doc=JsonDocument.Parse(capture.Summary());var state=doc.RootElement;
        Check(state.GetProperty("exitCode").GetInt32()==7&&!state.GetProperty("timedOut").GetBoolean(),"Nonzero exit is distinguished from timeout");
        Check(state.GetProperty("stdout").GetString()!.Length<=16384&&state.GetProperty("stderr").GetString()!.Length<=16384,"Both diagnostic tails are bounded");
        Check(!capture.Summary().Contains(new string('a',48)),"URLs and auth capabilities are redacted");
    }
    using(var capture=new DiagnosticCapture(Spawn("--hang"),Path.Combine(root,"timeout.json"))){
        capture.TimedOut=!capture.Process.WaitForExit(100);Check(capture.TimedOut,"Timeout is recorded explicitly");
        capture.Process.Kill(entireProcessTree:true);Check(capture.Process.WaitForExit(5000),"Timed-out probe is cleaned up");capture.Flush();capture.Save();
        Check(JsonDocument.Parse(capture.Summary()).RootElement.GetProperty("timedOut").GetBoolean(),"Saved diagnostics preserve timeout status");
    }
    Console.WriteLine($"PASS: {checks} launcher integrity/platform/diagnostic assertions");return 0;
}finally{Directory.Delete(root,true);}
