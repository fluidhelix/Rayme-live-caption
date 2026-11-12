using System.Collections.Concurrent;
using System.IO;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Windows.Automation;

static class Program{
    static int checks;
    static string package="",data="",model="";
    [DllImport("user32.dll")]static extern bool PostMessage(IntPtr window,uint message,IntPtr w,IntPtr l);
    static void Check(bool value,string why){if(!value)throw new Exception(why);checks++;Console.WriteLine("PASS "+why);}
    static void Until(Func<bool> predicate,string why,int seconds=30){var end=DateTime.UtcNow.AddSeconds(seconds);while(DateTime.UtcNow<end){if(predicate())return;Thread.Sleep(100);}throw new Exception("Timed out: "+why);}
    [STAThread]static int Main(string[] args){
        try{
            package=Path.GetFullPath(args[0]);model=Path.GetFullPath(args[1]);data=Path.Combine(Path.GetTempPath(),"Rayme données 日本 "+Guid.NewGuid());Directory.CreateDirectory(data);
            Check(package.Contains(' ')&&package.Any(c=>c>127),"Extracted ZIP is tested from a spaced, non-ASCII directory");
            using(var verify=Start("--diagnostics --verify-package")){Check(verify.WaitForExit(30000)&&verify.ExitCode==0,"Direct Rayme.exe verifies the complete ZIP without global runtime paths");}
            var browsers=BrowserPids();
            using(var session=new Session()){
                session.Wait("setup-required");var overlay=Overlay();int pid=overlay.Id;
                var pathBox=Find(pid,"Existing model path",ControlType.Edit);
                Check(!File.Exists(Path.Combine(data,"runtime.json")),"First run does not manufacture a model configuration");
                string invalid=Path.Combine(data,"invalid.bin");File.WriteAllText(invalid,"Not a model");Set(pathBox,invalid);Invoke(Find(pid,"Use this model",ControlType.Button));
                Until(()=>Find(pid,"Speech setup status",ControlType.Text).Current.HelpText.Contains("not a complete",StringComparison.OrdinalIgnoreCase)&&Find(pid,"Use this model",ControlType.Button).Current.IsEnabled,"Invalid model recovery");
                Check(!File.Exists(Path.Combine(data,"runtime.json")),"Invalid model selection cannot persist settings");
                Set(pathBox,model);Invoke(Find(pid,"Use this model",ControlType.Button));session.Wait("speech-ready",180);
                Until(()=>File.Exists(Path.Combine(data,"runtime.json")),"Validated model persistence");
                using(var saved=JsonDocument.Parse(File.ReadAllText(Path.Combine(data,"runtime.json"))))Check(saved.RootElement.GetProperty("model").GetString()==model,"Validated Unicode model path persists outside the package");
                int before=session.Events.Count(x=>x=="speech-ready");int oldWhisper=Process.GetProcessesByName("whisper-server").Single(UnderPackage).Id;
                SelectMode(pid,"CPU");session.WaitCount("speech-ready",before+1,180);
                Check(Gone(oldWhisper)&&Process.GetProcessesByName("whisper-server").Count(UnderPackage)==1,"Switching processing mode stops the previous Whisper before starting exactly one replacement");
                Until(()=>Find(pid,"Actual speech processing device",ControlType.Text).Current.HelpText.Contains("(CPU)"),"Actual CPU processing report");
                Check(Find(pid,"Actual speech processing device",ControlType.Text).Current.HelpText.Contains("(CPU)"),"CPU selection reports the actual initialized device");
                before=session.Events.Count(x=>x=="speech-ready");SelectMode(pid,"Auto — prefer compatible NVIDIA GPU");session.WaitCount("speech-ready",before+1,180);
                Until(()=>{using var saved=JsonDocument.Parse(File.ReadAllText(Path.Combine(data,"runtime.json")));return saved.RootElement.GetProperty("processingMode").GetString()=="auto";},"Successful processing-mode persistence");
                using(var saved=JsonDocument.Parse(File.ReadAllText(Path.Combine(data,"runtime.json"))))Check(saved.RootElement.GetProperty("processingMode").GetString()=="auto","Processing selection persists without editing .env");
                Invoke(Find(pid,"Close Speech setup",ControlType.Button));
                Invoke(Find(pid,"Open Controls",ControlType.Button));Invoke(Find(pid,"Close Controls",ControlType.Button));
                Check(!session.Process.HasExited,"Closing Controls does not end captioning");
                var window=FindWindow(pid,"Rayme Smart Live Captions");IntPtr hwnd=new(window.Current.NativeWindowHandle);
                Invoke(Find(pid,"Hide overlay",ControlType.Button));Thread.Sleep(200);Check(!session.Process.HasExited&&!overlay.HasExited,"Hide keeps the overlay and backend running");
                int[] owned=OwnedPids(session);PostMessage(hwnd,0x0010,IntPtr.Zero,IntPtr.Zero); // actual WPF Closing -> private intent -> backend shutdown
                Check(session.Process.WaitForExit(20000)&&session.Process.ExitCode==0,"Normal native window Quit Rayme completes without a console");
                Check(owned.All(Gone),"Explicit Quit leaves no owned Node, Whisper or overlay process");
            }
            using(var session=new Session()){
                session.Wait("speech-ready",180);Check(!session.Events.Contains("setup-required"),"Saved-model startup uses the local model without downloading");
                int[] owned=OwnedPids(session);Overlay().Kill();
                var cancel=Find(session.Process.Id,"Cancel",ControlType.Button,30);Check(owned.All(Gone),"Unexpected overlay failure stops owned processes before offering recovery");
                Invoke(cancel);Check(session.Process.WaitForExit(10000)&&session.Process.ExitCode==2,"Unexpected failure offers Cancel rather than pretending it was user Quit");
            }
            using(var session=new Session()){
                session.Wait("speech-ready",180);int[] owned=OwnedPids(session);session.Process.Kill();session.Process.WaitForExit();
                Until(()=>owned.All(Gone),"Kill-on-close Job cleanup");Check(owned.All(Gone),"Abrupt launcher termination cannot orphan its process tree");
            }
            Check(!BrowserPids().Except(browsers).Any(),"No Chrome, Edge or Firefox process opens on Quit or crash");
            Check(!Directory.EnumerateFiles(package,".env",SearchOption.AllDirectories).Any(),"Portable ZIP contains no .env");
            Console.WriteLine($"PASS: {checks} packaged Windows lifecycle/setup checks");return 0;
        }catch(Exception error){Console.Error.WriteLine(error);return 1;}
        finally{try{if(Directory.Exists(data))Directory.Delete(data,true);}catch(IOException){}}
    }
    static Process Start(string arguments){
        var start=new ProcessStartInfo(Path.Combine(package,"Rayme.exe"),arguments){UseShellExecute=false,CreateNoWindow=true,RedirectStandardInput=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=package};
        start.Environment["PATH"]=Environment.GetFolderPath(Environment.SpecialFolder.System);start.Environment["DOTNET_ROOT"]=Path.Combine(data,"not-installed");start.Environment["DOTNET_MULTILEVEL_LOOKUP"]="0";start.Environment["NODE_PATH"]="";start.Environment["RAYME_DATA_ROOT"]=data;
        return Process.Start(start)??throw new Exception("Rayme.exe could not be launched");
    }
    static Process Overlay(){Process? found=null;Until(()=>{found=Process.GetProcessesByName("CaptionOverlay").FirstOrDefault(p=>UnderPackage(p));return found!=null;},"Owned native overlay");return found!;}
    static bool UnderPackage(Process p){try{return p.MainModule?.FileName?.StartsWith(package,StringComparison.OrdinalIgnoreCase)==true;}catch{return false;}}
    static int[] OwnedPids(Session s)=>Process.GetProcesses().Where(p=>p.Id!=s.Process.Id&&UnderPackage(p)).Select(p=>p.Id).ToArray();
    static bool Gone(int pid){try{return Process.GetProcessById(pid).HasExited;}catch(ArgumentException){return true;}}
    static HashSet<int> BrowserPids()=>new[]{"chrome","msedge","firefox"}.SelectMany(Process.GetProcessesByName).Select(p=>p.Id).ToHashSet();
    static AutomationElement FindWindow(int pid,string title){AutomationElement? result=null;Until(()=>{result=AutomationElement.RootElement.FindFirst(TreeScope.Children,new AndCondition(new PropertyCondition(AutomationElement.ProcessIdProperty,pid),new PropertyCondition(AutomationElement.NameProperty,title)));return result!=null;},title);return result!;}
    static AutomationElement Find(int pid,string name,ControlType type,int seconds=30){
        AutomationElement? found=null;Until(()=>{
            foreach(AutomationElement window in AutomationElement.RootElement.FindAll(TreeScope.Children,new PropertyCondition(AutomationElement.ProcessIdProperty,pid))){
                found=window.FindFirst(TreeScope.Descendants,new AndCondition(new PropertyCondition(AutomationElement.NameProperty,name),new PropertyCondition(AutomationElement.ControlTypeProperty,type)));if(found!=null)return true;
            }return false;
        },name,seconds);return found!;
    }
    static void Set(AutomationElement field,string value)=>((ValuePattern)field.GetCurrentPattern(ValuePattern.Pattern)).SetValue(value);
    static void SelectMode(int pid,string label){var combo=Find(pid,"Speech processing mode",ControlType.ComboBox);((ExpandCollapsePattern)combo.GetCurrentPattern(ExpandCollapsePattern.Pattern)).Expand();AutomationElement? item=null;Until(()=>{item=combo.FindFirst(TreeScope.Descendants,new PropertyCondition(AutomationElement.NameProperty,label));return item!=null;},"Processing option "+label);((SelectionItemPattern)item!.GetCurrentPattern(SelectionItemPattern.Pattern)).Select();}
    static void Invoke(AutomationElement button)=>((InvokePattern)button.GetCurrentPattern(InvokePattern.Pattern)).Invoke();
    sealed class Session:IDisposable{
        public Process Process { get; }=Start("--diagnostics");
        public ConcurrentBag<string> Events { get; }=[];
        public Session(){_=Task.Run(async()=>{while(await Process.StandardOutput.ReadLineAsync() is {} line){try{using var doc=JsonDocument.Parse(line);Events.Add(doc.RootElement.GetProperty("type").GetString()!);}catch(JsonException){}}});_=Task.Run(async()=>{while(await Process.StandardError.ReadLineAsync()!=null){}});}
        public void Wait(string type,int seconds=45)=>Until(()=>Events.Contains(type),type,seconds);
        public void WaitCount(string type,int count,int seconds=45)=>Until(()=>Events.Count(e=>e==type)>=count,type+" count "+count,seconds);
        public void Dispose(){if(!Process.HasExited){Process.Kill();Process.WaitForExit();}Process.Dispose();}
    }
}
