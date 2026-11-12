using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Rayme.Testing;

// Drain continuously even after the retained tail fills; never wait on a full pipe.
internal sealed class DiagnosticCapture:IDisposable {
    const int Limit=16384;
    readonly StringBuilder stdout=new(),stderr=new();
    readonly Task outputTask,errorTask;
    readonly Stopwatch clock=Stopwatch.StartNew();
    readonly string file;
    readonly Func<string,string> sanitize;
    public Process Process { get; }
    public bool TimedOut { get; set; }
    public DiagnosticCapture(Process process,string file,Func<string,string>? sanitize=null,Action<string>? line=null){
        Process=process;this.file=file;this.sanitize=sanitize??(s=>s);
        outputTask=Drain(process.StandardOutput,stdout,line);errorTask=Drain(process.StandardError,stderr,null);
    }
    async Task Drain(StreamReader reader,StringBuilder tail,Action<string>? line){
        char[] buffer=new char[1024];var pending=new StringBuilder();int n;
        while((n=await reader.ReadAsync(buffer))>0){
            lock(tail){tail.Append(buffer,0,n);if(tail.Length>Limit)tail.Remove(0,tail.Length-Limit);}
            if(line!=null)for(int i=0;i<n;i++){if(buffer[i]=='\n'){line(pending.ToString());pending.Clear();}else if(pending.Length<8192)pending.Append(buffer[i]);}
        }
    }
    public void Flush(){try{Task.WhenAll(outputTask,errorTask).Wait(TimeSpan.FromSeconds(3));}catch(AggregateException){}}
    public string Summary(){
        string output,error;lock(stdout)output=stdout.ToString();lock(stderr)error=stderr.ToString();
        return JsonSerializer.Serialize(new{pid=Process.Id,elapsedMs=clock.ElapsedMilliseconds,timedOut=TimedOut,exited=Process.HasExited,exitCode=Process.HasExited?(int?)Process.ExitCode:null,retainedCharactersPerStream=Limit,stdout=Clean(output),stderr=Clean(error)});
    }
    string Clean(string text)=>Regex.Replace(sanitize(text),@"(?i)(https?://[^\s""<>]+|\b[a-f0-9]{48}\b)","[redacted]");
    public void Save(){Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);File.WriteAllText(file,Summary());}
    public void Terminate(){
        if(Process.HasExited)return;
        try{Process.Kill(entireProcessTree:true);}catch(InvalidOperationException)when(Process.HasExited){}
        if(!Process.WaitForExit(5000)){Save();throw new TimeoutException("Diagnostic process could not be terminated");}
    }
    public void Dispose(){
        Terminate();
        Flush();Save();Process.Dispose();
    }
}
