using System;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace Rayme.Overlay;

internal static class Program {
    [STAThread]
    static int Main(string[] args) {
        // WPF can render entirely on the CPU; no GPU or additional browser process needed.
        RenderOptions.ProcessRenderMode = RenderMode.SoftwareOnly;
#if RAYME_DISTRIBUTION
        bool smoke = false;
#else
        bool smoke = args.Length == 1 && args[0] == "--smoke-test";
#endif
        SessionClient? client = null;
        string capability = "";
        if (!smoke) {
            try {
                if (args.Length != 0 || !Console.IsInputRedirected) throw new InvalidOperationException();
                var line = new System.Text.StringBuilder(); int c;
                while ((c = Console.In.Read()) != -1 && c != '\n') { if (line.Length >= 4096) throw new InvalidOperationException(); line.Append((char)c); }
                using var doc = JsonDocument.Parse(line.ToString()); client = new SessionClient(doc.RootElement.GetProperty("url").GetString()!);
                capability = doc.RootElement.TryGetProperty("capability",out var intent) ? intent.GetString() ?? "" : "";
            } catch { Rayme.CharcoalDialog.Show("Start Rayme.exe from the complete portable application folder. A private local session handoff is required.", "Rayme captions",0x10); return 1; }
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
#if !RAYME_DISTRIBUTION
        if(smoke)app.DispatcherUnhandledException+=(_,e)=>{
            string folder=Environment.GetEnvironmentVariable("RAYME_UI_SMOKE_OUTPUT")??Path.Combine(Environment.CurrentDirectory,"artifacts","overlay-ui");Directory.CreateDirectory(folder);
            File.WriteAllText(Path.Combine(folder,"checks.json"),JsonSerializer.Serialize(new{passed=false,failure=e.Exception.ToString(),checks=Array.Empty<string>()}));e.Handled=true;app.Shutdown(1);
        };
#endif
        Theme.Install(app);
        var window = new OverlayWindow(client, smoke); app.MainWindow = window;
        window.ExitCapability=capability;
        if (!smoke) _ = Task.Run(async () => {
            // EOF on the parent's owned pipe also covers console-X or an unexpected Node exit.
            try { char[] b = new char[1]; while (await Console.In.ReadAsync(b,0,1) != 0) { } } catch (IOException) { }
            await app.Dispatcher.InvokeAsync(window.BackendClosed);
        });
        app.Run(window);
        return window.SmokePassed ? 0 : smoke ? 1 : 0;
    }
}
