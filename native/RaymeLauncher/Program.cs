using System.IO.Pipes;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using Microsoft.Win32.SafeHandles;

namespace Rayme.Launcher;

internal static class Program {
    [DllImport("user32.dll",CharSet=CharSet.Unicode)] static extern int MessageBox(IntPtr owner,string text,string title,uint flags);
    static bool diagnostics;
    static int diagnosticBytes;
    static readonly object diagnosticLock=new();
    static void Report(object data,bool error=false){
        if(!diagnostics)return;
        string line=JsonSerializer.Serialize(data);
        lock(diagnosticLock){
            // One session has a hard 32 KiB diagnostic budget; no raw backend text.
            if(Encoding.UTF8.GetByteCount(line)>4096||diagnosticBytes+Encoding.UTF8.GetByteCount(line)>32768)return;
            diagnosticBytes+=Encoding.UTF8.GetByteCount(line);
            var writer=error?Console.Error:Console.Out;writer.WriteLine(line);writer.Flush();
        }
    }
    [STAThread] internal static int Main(string[] args){
        diagnostics=args.Contains("--diagnostics");
        string stage="platform";
        try{
            Report(new{type="launcher-start",osVersion=Environment.OSVersion.Version.ToString(),architecture=RuntimeInformation.OSArchitecture.ToString()});
            if(!StartupChecks.Supported(OperatingSystem.IsWindows(),Environment.OSVersion.Version,RuntimeInformation.OSArchitecture)){
                Report(new{type="launcher-failure",stage,reason="unsupported-platform",minimumWindowsBuild=22000},true);
                if(!diagnostics&&OperatingSystem.IsWindows())MessageBox(IntPtr.Zero,"Rayme requires Windows 11 on an x64 PC. ARM and earlier Windows versions are not supported by this package.","Rayme",0x10);
                return 1;
            }
            stage="single-instance";
            using var single=new Mutex(true,"Local\\RaymeSmartLiveCaptions",out bool first);
            if(!first){
                Report(new{type="launcher-failure",stage,reason="already-running"},true);
                if(!diagnostics)MessageBox(IntPtr.Zero,"Rayme is already running. Use its notification-area icon to show captions or quit.","Rayme",0x40);
                return diagnostics?3:0;
            }
            string root=AppContext.BaseDirectory;
            stage="package-integrity";
            var clock=System.Diagnostics.Stopwatch.StartNew();long last=-1000;
            Report(new{type="package-verification-started"});
            StartupChecks.Verify(root,entry=>{if(clock.ElapsedMilliseconds-last>=1000){last=clock.ElapsedMilliseconds;Report(new{type="package-verification-progress",entry=StartupChecks.SafeEntry(entry),elapsedMs=clock.ElapsedMilliseconds});}});
            Report(new{type="package-verified",elapsedMs=clock.ElapsedMilliseconds});
            if(args.Contains("--verify-package"))return 0;
            stage="backend-session";
            while(true){
                string? failure=Run(root).GetAwaiter().GetResult();
                if(failure==null)return 0;
                Report(new{type="session-failed",reason="backend-or-overlay-stopped"},true);
                if(diagnostics)return 2;
                if(MessageBox(IntPtr.Zero,failure+"\n\nAll Rayme capture and speech processes have been stopped. Retry starts a fresh session; Cancel quits. No browser will be opened.","Rayme · Session stopped",0x10015)!=4)return 2;
            }
        }catch(Exception error){
            Report(StartupChecks.Failure(stage,error),true);
            if(!diagnostics)MessageBox(IntPtr.Zero,"Rayme could not start or the package integrity check failed. Extract the complete Rayme ZIP into a new folder and try again. Keep all files in App together; do not copy only Rayme.exe.","Rayme · Startup problem",0x10010);
            return 1;
        }
    }
    static async Task<string?> Run(string root){
        using var input=new AnonymousPipeServerStream(PipeDirection.Out,HandleInheritability.Inheritable);
        using var output=new AnonymousPipeServerStream(PipeDirection.In,HandleInheritability.Inheritable);
        using var errors=new AnonymousPipeServerStream(PipeDirection.In,HandleInheritability.Inheritable);
        using var job=new Job();
        string node=Path.Combine(root,"App","node","node.exe"),script=Path.Combine(root,"App","backend","main.js");
        var environment=new SortedDictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        foreach(string key in new[]{"SystemRoot","WINDIR","TEMP","TMP","LOCALAPPDATA","APPDATA","USERPROFILE","PROGRAMDATA","ProgramFiles","ProgramW6432","COMSPEC","NUMBER_OF_PROCESSORS","PROCESSOR_ARCHITECTURE"})if(Environment.GetEnvironmentVariable(key) is {} value)environment[key]=value;
        environment["PATH"]=Path.Combine(root,"App","node")+";"+Environment.GetFolderPath(Environment.SpecialFolder.System);
        environment["RAYME_PORTABLE"]="1";environment["RAYME_APP_ROOT"]=Path.Combine(root,"App");
        environment["RAYME_DATA_ROOT"]=Environment.GetEnvironmentVariable("RAYME_DATA_ROOT")??Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),"Rayme","Runtime");
        using var child=job.Start(node,script,Path.GetDirectoryName(script)!,environment,input.ClientSafePipeHandle,output.ClientSafePipeHandle,errors.ClientSafePipeHandle);
        input.DisposeLocalCopyOfClientHandle();output.DisposeLocalCopyOfClientHandle();errors.DisposeLocalCopyOfClientHandle();
        Report(new{type="backend-process",pid=child.Pid});
        bool userQuit=false;string? failure=null;
        using var deadline=new CancellationTokenSource();using var enforce=deadline.Token.Register(job.Stop);
        var read=ReadLines(output,line=>{
            try{using var doc=JsonDocument.Parse(line);var data=doc.RootElement;string? type=data.GetProperty("type").GetString();
                if(type=="session-ended"&&data.GetProperty("reason").GetString()=="user-quit")userQuit=true;
                if(type=="shutdown-started"){if(data.GetProperty("reason").GetString()=="user-quit")userQuit=true;deadline.CancelAfter(TimeSpan.FromSeconds(15));}
                if(type=="failure")failure=(data.GetProperty("message").GetString()??"Rayme could not complete startup.")[..Math.Min(1024,data.GetProperty("message").GetString()!.Length)];
                if(type is "session-started" or "setup-required" or "speech-ready" or "session-ended")Report(new{type});
            }catch(JsonException){}catch(InvalidOperationException){}catch(KeyNotFoundException){}
        });
        var drain=ReadLines(errors,_=>{});
        await Task.Run(()=>child.Wait());
        Report(new{type="backend-exited",exitCode=child.ExitCode});
        deadline.CancelAfter(Timeout.InfiniteTimeSpan);
        job.Stop(); // Reap any remaining descendant before reporting success, failure or Retry.
        await Task.WhenAll(read,drain).WaitAsync(TimeSpan.FromSeconds(5));
        Report(new{type="owned-processes-stopped"});
        return userQuit?null:failure??"Rayme's backend or caption window stopped unexpectedly.";
    }
    static async Task ReadLines(Stream stream,Action<string> line){
        using var reader=new StreamReader(stream,Encoding.UTF8,false,4096,true);char[] buffer=new char[1024];var pending=new StringBuilder();int read;
        while((read=await reader.ReadAsync(buffer))>0)for(int i=0;i<read;i++){
            char c=buffer[i];if(c=='\n'){line(pending.ToString());pending.Clear();}else if(pending.Length<8192)pending.Append(c);
        }
    }
}

// The backend is created suspended, placed in a kill-on-close Job, THEN resumed.
// No child can spawn outside ownership during the Process.Start/AssignJob race.
internal sealed class Job:IDisposable{
    readonly SafeFileHandle handle;
    [StructLayout(LayoutKind.Sequential)] struct BasicLimits { public long ProcessTime,JobTime;public uint Flags;public UIntPtr MinWorkingSet,MaxWorkingSet;public uint ActiveProcesses;public UIntPtr Affinity;public uint Priority,Scheduling; }
    [StructLayout(LayoutKind.Sequential)] struct IoCounters{public ulong ReadOps,WriteOps,OtherOps,ReadBytes,WriteBytes,OtherBytes;}
    [StructLayout(LayoutKind.Sequential)] struct Limits{public BasicLimits Basic;public IoCounters Io;public UIntPtr ProcessMemory,JobMemory,PeakProcess,PeakJob;}
    [StructLayout(LayoutKind.Sequential)] struct Startup{public uint Size;public IntPtr Reserved,Desktop,Title;public uint X,Y,XSize,YSize,XChars,YChars,Fill,Flags;public ushort Show,ReservedSize;public IntPtr ReservedData,Input,Output,Error;}
    [StructLayout(LayoutKind.Sequential)] struct ProcessInfo{public IntPtr Process,Thread;public uint Pid,Tid;}
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern SafeFileHandle CreateJobObject(IntPtr attributes,string? name);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool SetInformationJobObject(SafeFileHandle job,int kind,ref Limits limits,uint size);
    [DllImport("kernel32.dll",SetLastError=true)] static extern bool AssignProcessToJobObject(SafeFileHandle job,IntPtr process);
    [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] static extern bool CreateProcess(string app,StringBuilder command,IntPtr processAttributes,IntPtr threadAttributes,bool inherit,uint flags,IntPtr environment,string directory,ref Startup startup,out ProcessInfo process);
    [DllImport("kernel32.dll",SetLastError=true)] static extern uint ResumeThread(IntPtr thread);
    [DllImport("kernel32.dll")] internal static extern uint WaitForSingleObject(IntPtr handle,uint milliseconds);
    [DllImport("kernel32.dll")] internal static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll")] static extern bool TerminateProcess(IntPtr process,uint code);
    [DllImport("kernel32.dll")] static extern bool TerminateJobObject(SafeFileHandle job,uint code);
    public Job(){handle=CreateJobObject(IntPtr.Zero,null);var limits=new Limits{Basic=new BasicLimits{Flags=0x2000}};if(handle.IsInvalid||!SetInformationJobObject(handle,9,ref limits,(uint)Marshal.SizeOf<Limits>()))throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(),"Cannot supervise the Rayme process tree");}
    public Child Start(string exe,string script,string directory,SortedDictionary<string,string> environment,SafePipeHandle input,SafePipeHandle output,SafePipeHandle error){
        var startup=new Startup{Size=(uint)Marshal.SizeOf<Startup>(),Flags=0x100,Input=input.DangerousGetHandle(),Output=output.DangerousGetHandle(),Error=error.DangerousGetHandle()};
        string block=string.Join('\0',environment.Select(x=>x.Key+"="+x.Value))+"\0\0";IntPtr memory=Marshal.StringToHGlobalUni(block);
        try{
            if(!CreateProcess(exe,new StringBuilder('"'+exe+"\" \""+script+'"'),IntPtr.Zero,IntPtr.Zero,true,0x08000000|0x4|0x400,memory,directory,ref startup,out var info))throw new System.ComponentModel.Win32Exception(Marshal.GetLastPInvokeError(),"Could not start bundled Node runtime");
            try{if(!AssignProcessToJobObject(handle,info.Process)||ResumeThread(info.Thread)==uint.MaxValue){int code=Marshal.GetLastPInvokeError();TerminateProcess(info.Process,1);throw new System.ComponentModel.Win32Exception(code,"Could not establish process ownership");}return new Child(info.Process,info.Pid);}
            catch{CloseHandle(info.Process);throw;}finally{CloseHandle(info.Thread);}
        }finally{Marshal.FreeHGlobal(memory);}
    }
    public void Stop(){if(!handle.IsClosed&&!handle.IsInvalid)TerminateJobObject(handle,0);}
    public void Dispose(){Stop();handle.Dispose();}
}
internal sealed class Child(IntPtr handle,uint pid):IDisposable{
    [DllImport("kernel32.dll")] static extern bool GetExitCodeProcess(IntPtr process,out uint code);
    public uint? ExitCode=>GetExitCodeProcess(handle,out var code)?code:null;
    public uint Pid=>pid;
    public void Wait()=>Job.WaitForSingleObject(handle,uint.MaxValue);
    public void Dispose()=>Job.CloseHandle(handle);
}
