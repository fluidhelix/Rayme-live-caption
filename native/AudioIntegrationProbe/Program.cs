using System.Diagnostics;
using System.Buffers.Binary;
using System.Text.Json;
using NAudio.CoreAudioApi;
using NAudio.Wave;

// TEST TOOL ONLY. Uses documented render/capture APIs; never changes devices, volume or security settings.
static class Program
{
    static void Emit(object data) => Console.WriteLine(JsonSerializer.Serialize(data));
    static object Error(Exception ex) => new { message = ex.Message, hresult = $"0x{ex.HResult:X8}" };
    static async Task<int> Main(string[] args)
    {
        try {
            if (args.Length == 1 && args[0] == "--environment") {
                try {
                    using var e = new MMDeviceEnumerator();
                    var list = new List<object>();
                    foreach (var endpoint in e.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active)) {
                        using (endpoint) list.Add(new { id = endpoint.ID, name = endpoint.FriendlyName });
                    }
                    Emit(new { os = Environment.OSVersion.ToString(), supported = OperatingSystem.IsWindowsVersionAtLeast(10,0,22000), endpoints = list });
                } catch(Exception ex) { Emit(new { os = Environment.OSVersion.ToString(), error = Error(ex) }); }
                return 0;
            }
            if (args.Length == 2 && args[0] == "--activate") {
                if (!OperatingSystem.IsWindowsVersionAtLeast(10,0,22000)) { Emit(new { ok = false, error = new { unsupported = true, message = "Process probe requires Windows build 22000+" } }); return 0; }
                try {
                    using var capture = await new WasapiRecorderBuilder().WithProcessLoopback(uint.Parse(args[1]), ProcessLoopbackMode.IncludeTargetProcessTree).BuildAsync().WaitAsync(TimeSpan.FromSeconds(18));
                    Exception? failure = null;
                    capture.RecordingStopped += (_, e) => failure = e.Exception;
                    capture.StartRecording(); await Task.Delay(300); capture.StopRecording();
                    if (failure != null) throw failure;
                    Emit(new { ok = true });
                } catch(Exception ex) { Emit(new { ok = false, error = Error(ex) }); }
                return 0;
            }
            if (args.Length == 2 && args[0] == "--render-reference") {
                try {
                    using var e = new MMDeviceEnumerator(); using var endpoint = e.GetDevice(args[1]);
                    using var capture = await new WasapiRecorderBuilder().WithDevice(endpoint).WithLoopbackCapture().BuildAsync();
                    var format = capture.WaveFormat is WaveFormatExtensible ext ? ext.ToStandardWaveFormat() : capture.WaveFormat;
                    var meter = new DirectMeter(format); Exception? failure = null;
                    capture.DataAvailable += (buffer, flags, position, qpc) => meter.Add(buffer);
                    capture.RecordingStopped += (_, stopped) => failure = stopped.Exception;
                    capture.StartRecording(); await Task.Delay(3000); capture.StopRecording();
                    if (failure != null) throw failure;
                    Emit(new { ok = true, metrics = meter.Result() });
                } catch(Exception ex) { Emit(new { ok = false, error = Error(ex) }); }
                return 0;
            }
            if (args.Length != 4 || (args[0] != "--tone" && args[0] != "--fixture") || args[2] != "--endpoint") throw new ArgumentException("Use --environment, --activate <pid>, or --tone <Hz>/--fixture <wav> --endpoint <id>");
            using var self = Process.GetCurrentProcess();
            Emit(new { type = "identity", pid = self.Id, started = self.StartTime.ToUniversalTime().Ticks.ToString() });
            using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            WasapiPlayer? player = null; WaveFileReader? reader = null; MMDevice? selectedEndpoint = null;
            try {
                while (!watchdog.IsCancellationRequested) {
                    string? command = await Console.In.ReadLineAsync(watchdog.Token);
                    if (command == null || command == "quit") break;
                    if (command == "stop") { player?.Stop(); Emit(new { type = "stopped" }); continue; }
                    if (command != "play") continue;
                    try {
                        if (player != null) throw new InvalidOperationException("Producer is single-use");
                        using var enumerator = new MMDeviceEnumerator();
                        selectedEndpoint = enumerator.GetDevice(args[3]);
                        player = new WasapiPlayerBuilder().WithDevice(selectedEndpoint).WithSharedMode().Build();
                        IWaveProvider source;
                        if (args[0] == "--tone") source = new Tone(double.Parse(args[1],System.Globalization.CultureInfo.InvariantCulture));
                        else { reader = new WaveFileReader(args[1]); source = new Loop(reader); }
                        player.PlaybackStopped += (_,e) => { if (e.Exception != null) Emit(new { type = "play-error", error = Error(e.Exception) }); };
                        player.Init(source); player.Play(); Emit(new { type = "playing" });
                    } catch(Exception ex) { Emit(new { type = "play-error", error = Error(ex) }); }
                }
            } finally { player?.Dispose(); reader?.Dispose(); selectedEndpoint?.Dispose(); }
            return 0;
        } catch(Exception ex) { Emit(new { type = "fatal", error = Error(ex) }); return 1; }
    }
    // Independent reference analyzes the endpoint's native PCM/float format directly.
    // It does NOT use production AudioConversion or save sampled data.
    sealed class DirectMeter(WaveFormat format)
    {
        long frames; double power; readonly double[] real = new double[2], imag = new double[2];
        readonly int[] frequencies = [440,997]; readonly object gate = new();
        public void Add(ReadOnlySpan<byte> data) {
            lock(gate) {
                int bytes = format.BitsPerSample/8;
                for(int offset=0;offset+format.BlockAlign<=data.Length;offset+=format.BlockAlign,frames++) {
                    double x=0;
                    for(int c=0;c<format.Channels;c++) {
                        var sample=data.Slice(offset+c*bytes,bytes);
                        if(format.Encoding==WaveFormatEncoding.IeeeFloat && bytes==4) x+=BinaryPrimitives.ReadSingleLittleEndian(sample)*32768;
                        else if(format.Encoding==WaveFormatEncoding.Pcm && bytes==2) x+=BinaryPrimitives.ReadInt16LittleEndian(sample);
                        else if(format.Encoding==WaveFormatEncoding.Pcm && bytes==4) x+=BinaryPrimitives.ReadInt32LittleEndian(sample)/65536.0;
                        else if(format.Encoding==WaveFormatEncoding.Pcm && bytes==3) { int v=sample[0]|sample[1]<<8|sample[2]<<16; if((v&0x800000)!=0)v|=unchecked((int)0xFF000000);x+=v/256.0; }
                        else throw new NotSupportedException($"Unsupported reference format: {format}");
                    }
                    x/=format.Channels;power+=x*x;
                    for(int k=0;k<2;k++){double phase=2*Math.PI*frequencies[k]*frames/format.SampleRate;real[k]+=x*Math.Cos(phase);imag[k]+=x*Math.Sin(phase);}
                }
            }
        }
        public object Result() { lock(gate) return new { samples=frames, sampleRate=format.SampleRate, rms=frames==0?0:Math.Sqrt(power/frames), tones=real.Select((r,k)=>frames==0?0:2*Math.Sqrt(r*r+imag[k]*imag[k])/frames).ToArray() }; }
    }
    sealed class Tone(double frequency) : IWaveProvider
    {
        public WaveFormat WaveFormat { get; } = new(48000,16,2);
        long frame;
        public int Read(Span<byte> buffer) {
            int frames = buffer.Length / 4;
            for(int i=0;i<frames;i++,frame++) { short sample=(short)(5000*Math.Sin(2*Math.PI*frequency*frame/48000)); BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(i*4,2),sample); BinaryPrimitives.WriteInt16LittleEndian(buffer.Slice(i*4+2,2),sample); }
            return frames*4;
        }
    }
    sealed class Loop(WaveFileReader reader) : IWaveProvider
    {
        public WaveFormat WaveFormat => reader.WaveFormat;
        public int Read(Span<byte> buffer) {
            int written=0;
            while(written<buffer.Length) { int n=reader.Read(buffer[written..]); if(n==0){reader.Position=0;if(reader.Length==0)break;}else written+=n; }
            return written;
        }
    }
}
