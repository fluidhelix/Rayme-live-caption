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
        bool smoke = args.Length == 1 && args[0] == "--smoke-test";
        SessionClient? client = null;
        if (!smoke) {
            try {
                if (args.Length != 0 || !Console.IsInputRedirected) throw new InvalidOperationException();
                var line = new System.Text.StringBuilder(); int c;
                while ((c = Console.In.Read()) != -1 && c != '\n') { if (line.Length >= 4096) throw new InvalidOperationException(); line.Append((char)c); }
                using var doc = JsonDocument.Parse(line.ToString()); client = new SessionClient(doc.RootElement.GetProperty("url").GetString()!);
            } catch { MessageBox.Show("Start the overlay using START WINDOWS CAPTIONS.bat. A private local session handoff is required.", "Rayme captions"); return 1; }
        }
        var app = new Application { ShutdownMode = ShutdownMode.OnMainWindowClose };
        var window = new OverlayWindow(client, smoke); app.MainWindow = window;
        if (!smoke) _ = Task.Run(async () => {
            // EOF on the parent's owned pipe also covers console-X or an unexpected Node exit.
            try { char[] b = new char[1]; while (await Console.In.ReadAsync(b,0,1) != 0) { } } catch (IOException) { }
            await app.Dispatcher.InvokeAsync(window.Close);
        });
        app.Run(window);
        return window.SmokePassed ? 0 : smoke ? 1 : 0;
    }
}
