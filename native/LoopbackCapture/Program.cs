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
        public int Read(byte[] buffer, int offset, int count) => input.Read(buffer, offset, count);
    }
    sealed class MixToMono(ISampleProvider input) : ISampleProvider
    {
        public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(input.WaveFormat.SampleRate, 1);
        private float[] scratch = [];
        public int Read(float[] buffer, int offset, int count)
        {
            int channels = input.WaveFormat.Channels;
            int wanted = count * channels;
            if (scratch.Length < wanted) scratch = new float[wanted];
            int read = input.Read(scratch, 0, wanted), frames = read / channels;
            for (int i = 0; i < frames; i++) {
                float sum = 0; for (int c = 0; c < channels; c++) sum += scratch[i * channels + c];
                buffer[offset + i] = sum / channels;
            }
            return frames;
        }
    }
}
public static class Program
{
    static void Status(string type, string message) => Console.Error.WriteLine(JsonSerializer.Serialize(new { type, message }));
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
            if (args.Length != 2 || args[0] != "--device") throw new ArgumentException("Use --list or --device <endpoint ID or default>.");
            using var endpoint = args[1] == "default" ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia) : enumerator.GetDevice(args[1]);
            if (endpoint.DataFlow != DataFlow.Render) throw new ArgumentException("Only playback endpoints can be captured.");
            using var capture = new WasapiLoopbackCapture(endpoint);
            var input = new BufferedWaveProvider(capture.WaveFormat) { BufferDuration = TimeSpan.FromSeconds(2), ReadFully = true, DiscardOnBufferOverflow = false };
            var converter = AudioConversion.Create(input);
            using var cancel = new CancellationTokenSource();
            var gate = new object(); Exception? failure = null;
            capture.DataAvailable += (_, e) => {
                lock (gate) {
                    try { input.AddSamples(e.Buffer, 0, e.BytesRecorded); }
                    catch (Exception ex) { failure = ex; cancel.Cancel(); }
                }
            };
            capture.RecordingStopped += (_, e) => { failure = e.Exception ?? new IOException("Playback device stopped or was removed."); cancel.Cancel(); };
            Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };
            // Parent closes stdin or writes a line on shutdown. No named global resources.
            _ = Task.Run(async () => { await Console.In.ReadLineAsync(); cancel.Cancel(); });
            using var output = Console.OpenStandardOutput();
            byte[] pcm = new byte[3200];
            capture.StartRecording(); Status("ready", "Capturing selected playback output (16 kHz mono PCM).");
            try
            {
                using var clock = new PeriodicTimer(TimeSpan.FromMilliseconds(100));
                while (await clock.WaitForNextTickAsync(cancel.Token))
                {
                    int count;
                    lock (gate) { count = converter.Read(pcm, 0, pcm.Length); }
                    if (count > 0) { await output.WriteAsync(pcm.AsMemory(0, count), cancel.Token); await output.FlushAsync(cancel.Token); }
                }
            }
            catch (OperationCanceledException) { }
            finally { capture.StopRecording(); }
            if (failure != null) { Status("disconnected", "Audio endpoint stopped or capture buffer overflowed. Reconnecting."); return 2; }
            return 0;
        }
        catch (Exception ex) { Status("error", ex.Message); return 1; }
    }
}
