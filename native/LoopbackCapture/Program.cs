using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

public static class AudioConversion
{
    // NAudio's managed WDL resampler preserves phase across reads and low-pass filters.
    // WaveToSampleProvider supports common Windows PCM/IEEE float and extensible formats.
    public static IWaveProvider Create(IWaveProvider source)
    {
        ISampleProvider mono = new MixToMono(new StandardFormat(source).ToSampleProvider());
        if (mono.WaveFormat.SampleRate != 16000) mono = new WdlResamplingSampleProvider(mono, 16000);
        return new SampleToWaveProvider16(mono);
    }
    sealed class StandardFormat(IWaveProvider input) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = input.WaveFormat is WaveFormatExtensible ext ? ext.ToStandardWaveFormat() : input.WaveFormat;
        public int Read(Span<byte> buffer) => input.Read(buffer);
    }
    sealed class MixToMono(ISampleProvider input) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(input.WaveFormat.SampleRate, 1);
        private float[] scratch = [];
        public int Read(Span<float> buffer)
        {
            int channels = input.WaveFormat.Channels;
            int wanted = buffer.Length * channels;
            if (scratch.Length < wanted) scratch = new float[wanted];
            int read = input.Read(scratch.AsSpan(0, wanted)), frames = read / channels;
            for (int i = 0; i < frames; i++) {
                float sum = 0; for (int c = 0; c < channels; c++) sum += scratch[i * channels + c];
                buffer[i] = sum / channels;
            }
            return frames;
        }
    }
}
public static class Program
{
    static void Status(string type, string message) => Console.Error.WriteLine(JsonSerializer.Serialize(new { type, message }));
    static void RequestStop(CancellationTokenSource shutdown)
    {
        lock (shutdown)
        {
            try { shutdown.Cancel(); } catch (ObjectDisposedException) { }
        }
    }
    static async Task WaitForParent(CancellationTokenSource shutdown)
    {
        try { await Console.In.ReadLineAsync(); } catch (IOException) { }
        RequestStop(shutdown);
    }
    public static async Task<int> Main(string[] args)
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            if (args.Length == 1 && args[0] == "--list")
            {
                string? defaultId = null;
                try { using var d = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia); defaultId = d.ID; } catch { }
                var devices = enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active);
                var list = new List<object>();
                foreach (var device in devices) { using (device) list.Add(new { id = device.ID, name = device.FriendlyName, isDefault = device.ID == defaultId }); }
                Console.WriteLine(JsonSerializer.Serialize(list)); return 0;
            }
            if (args.Length == 1 && args[0] == "--apps") {
                var parents = Discovery.ParentMap();
                Console.WriteLine(JsonSerializer.Serialize(new { supported = OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000), apps = Discovery.Applications(enumerator).Select(p => new { id = p.Id, pid = p.Pid, started = p.Started, name = p.Name, ancestors = Discovery.AncestorIds(p, parents, Discovery.Identity) }) })); return 0;
            }
            bool processMode = args.Length == 4 && args[0] == "--process" && args[2] == "--started";
            if (!processMode && (args.Length != 2 || args[0] != "--device")) throw new ArgumentException("Use --list, --apps, --device <endpoint>, or --process <pid> --started <ticks>.");
            int pid = processMode ? int.Parse(args[1]) : 0;
            string started = processMode ? args[3] : "";
            var shutdown = new CancellationTokenSource();
            ConsoleCancelEventHandler cancelKey = (_, e) => { e.Cancel = true; RequestStop(shutdown); };
            Console.CancelKeyPress += cancelKey;
            // Parent closes stdin or writes a line on shutdown. No named global resources.
            _ = Task.Run(() => WaitForParent(shutdown));
            try { return await Record(enumerator, processMode ? null : args[1], pid, started, shutdown.Token); }
            finally
            {
                Console.CancelKeyPress -= cancelKey;
                lock (shutdown) shutdown.Dispose();
            }
        }
        catch (Exception ex) { Status("error", $"Capture or activation failed: {ex.Message}"); return args.Length > 0 && args[0] == "--device" ? 2 : 4; }
    }

    static async Task<int> Record(MMDeviceEnumerator enumerator, string? selection, int pid, string started, CancellationToken shutdown)
    {
        bool processMode = selection == null;
        var watch = DefaultEndpointChange.ForSelection(selection);
        // Subscribe before resolving the endpoint so switching during lookup or
        // activation cannot strand us on a still-healthy former default.
        using var notifications = watch == null ? null : enumerator.CreateNotificationClient(useSynchronizationContext: false);
        if (notifications != null) notifications.DefaultDeviceChanged += (_, e) => watch!.Observe(e.Flow, e.Role, e.DeviceId);
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(shutdown);
        try
        {
            if (shutdown.IsCancellationRequested) return 0;
            using var endpoint = processMode ? null : watch != null ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(selection!);
            if (watch != null) watch.Bind(endpoint!.ID);
            if (watch?.Changed == true) return DefaultEndpointChange.ExitCode;
            if (endpoint != null && endpoint.DataFlow != DataFlow.Render) throw new ArgumentException("Only playback endpoints can be captured.");
            var builder = new WasapiRecorderBuilder();
            if (processMode)
            {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000)) { Status("unsupported", "Application capture requires Windows 11; whole-device capture remains available."); return 4; }
                if (!Discovery.Matches(pid, started)) { Status("terminated", "Selected process exited, was replaced or cannot be inspected. Refresh and select again."); return 3; }
                builder.WithProcessLoopback((uint)pid, ProcessLoopbackMode.IncludeTargetProcessTree);
            }
            else builder.WithDevice(endpoint!).WithLoopbackCapture();
            // Process activation must be asynchronous. Never substitute endpoint audio on failure.
            using var capture = processMode
                ? await CapturePolicy.ActivateChecked(pid, started, Discovery.Identity, () => builder.BuildAsync().WaitAsync(TimeSpan.FromSeconds(15)))
                : await builder.BuildAsync();
            if (shutdown.IsCancellationRequested) return 0;
            if (watch?.Changed == true) return DefaultEndpointChange.ExitCode;
            if (processMode && !Discovery.Matches(pid, started)) { Status("terminated", "Process changed during activation."); return 3; }
            var input = new BufferedWaveProvider(capture.WaveFormat, TimeSpan.FromSeconds(2)) { ReadFully = true, DiscardOnBufferOverflow = false };
            var converter = AudioConversion.Create(input);
            var gate = new object(); Exception? failure = null;
            capture.DataAvailable += (buffer, flags, devicePosition, qpcPosition) => {
                lock (gate) {
                    if (watch?.Changed == true || cancel.IsCancellationRequested) return;
                    try { input.AddSamples(buffer); }
                    catch (Exception ex) { failure = ex; cancel.Cancel(); }
                }
            };
            capture.RecordingStopped += (_, e) => { failure = e.Exception ?? new IOException("Playback device stopped or was removed."); cancel.Cancel(); };
            using var output = Console.OpenStandardOutput();
            byte[] pcm = new byte[3200];
            capture.StartRecording(); Status("ready", "Capturing selected source (16 kHz mono PCM).");
            try
            {
                using var clock = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                while (await clock.WaitForNextTickAsync(cancel.Token))
                {
                    if (watch?.Changed == true) break;
                    if (processMode && !Discovery.Matches(pid, started)) { Status("terminated", "Selected process ended. Refresh applications and select the new identity."); return 3; }
                    int count;
                    lock (gate) { count = converter.Read(pcm); }
                    if (count > 0 && watch?.Changed != true) { await output.WriteAsync(pcm.AsMemory(0, count), cancel.Token); await output.FlushAsync(cancel.Token); }
                }
            }
            catch (OperationCanceledException) { }
            finally { capture.StopRecording(); }
            if (shutdown.IsCancellationRequested) return 0;
            if (watch?.Changed == true) return DefaultEndpointChange.ExitCode;
            if (failure != null) { Status("disconnected", "Audio endpoint stopped or capture buffer overflowed. Reconnecting."); return 2; }
            return 0;
        }
        catch (Exception) when (shutdown.IsCancellationRequested) { return 0; }
    }
}
