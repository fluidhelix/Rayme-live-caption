using NAudio.Wave;
// Synthetic tone only: verifies managed WDL conversion without an endpoint.
foreach (var format in new[] { WaveFormat.CreateIeeeFloatWaveFormat(48000, 2), new WaveFormat(44100, 16, 2), new WaveFormat(48000, 24, 2), new WaveFormat(16000, 16, 1), new WaveFormatExtensible(48000, 32, 2) })
{
    var bytes = new byte[format.AverageBytesPerSecond];
    int sampleBytes = format.BitsPerSample / 8;
    for (int frame = 0; frame < format.SampleRate; frame++)
    for (int channel = 0; channel < format.Channels; channel++)
    {
        double sample = .3 * Math.Sin(2 * Math.PI * 440 * frame / format.SampleRate);
        int offset = (frame * format.Channels + channel) * sampleBytes;
        if (format.Encoding == WaveFormatEncoding.IeeeFloat || format.Encoding == WaveFormatEncoding.Extensible) BitConverter.GetBytes((float)sample).CopyTo(bytes, offset);
        else if (sampleBytes == 2) BitConverter.GetBytes((short)(sample * 32767)).CopyTo(bytes, offset);
        else { int v = (int)(sample * 8388607); bytes[offset] = (byte)v; bytes[offset + 1] = (byte)(v >> 8); bytes[offset + 2] = (byte)(v >> 16); }
    }
    using var source = new RawSourceWaveStream(bytes, 0, bytes.Length, format);
    var resampler = AudioConversion.Create(source);
    using var output = new MemoryStream(); byte[] block = new byte[3200]; int count;
    while ((count = resampler.Read(block)) > 0) output.Write(block, 0, count);
    var pcm = output.ToArray();
    if (Math.Abs(pcm.Length - 32000) > 1600 || pcm.Length % 2 != 0) throw new Exception("Incorrect output duration/alignment");
    double power = 0; for (int i = 0; i < pcm.Length; i += 2) power += Math.Pow(BitConverter.ToInt16(pcm, i), 2);
    double rms = Math.Sqrt(power / (pcm.Length / 2));
    if (rms < 5000 || rms > 9000) throw new Exception("Incorrect sample amplitude/channel conversion");
    Console.WriteLine($"PASS {format} -> 16 kHz mono PCM");
}
using (var silence = new RawSourceWaveStream(new byte[192000], 0, 192000, WaveFormat.CreateIeeeFloatWaveFormat(48000, 1)))

{
    var resampler = AudioConversion.Create(silence);
    byte[] block = new byte[3200]; int n;
    while ((n = resampler.Read(block)) > 0) if (block.Take(n).Any(b => b != 0)) throw new Exception("Silence conversion introduced audio");
    Console.WriteLine("PASS silence conversion");
}

var identities = new Dictionary<int, AppIdentity> { [10] = new(10, "100", "chrome"), [11] = new(11, "101", "chrome"), [20] = new(20, "200", "Discord") };
AppIdentity? Lookup(int pid) => identities.GetValueOrDefault(pid);
var apps = Discovery.Candidates(new[] { 0, 11, 11, 20, 999 }, new Dictionary<int,int> { [11] = 10 }, Lookup);
if (apps.Length != 2 || !apps.Any(a => a.Pid == 10) || apps.Any(a => a.Pid == 999)) throw new Exception("Session discovery/root/dedup failed");
Console.WriteLine("PASS session discovery, process-tree grouping, deduplication and inaccessible metadata");
if (new AppIdentity(10, "100", "chrome").Id == new AppIdentity(10, "300", "chrome").Id) throw new Exception("PID reuse not distinguished");
Console.WriteLine("PASS PID reuse creates a distinct identity");
try { await CapturePolicy.ActivateChecked(999, "1", Lookup, () => Task.FromResult(new DisposableProbe())); throw new Exception("Missing process accepted"); }
catch (InvalidOperationException) { }
try { await CapturePolicy.ActivateChecked<DisposableProbe>(10, "100", Lookup, () => throw new IOException("activation denied")); throw new Exception("Activation failure swallowed"); }
catch (IOException) { }
var probe = new DisposableProbe();
try { await CapturePolicy.ActivateChecked(10, "100", Lookup, () => { identities[10] = new(10, "300", "chrome"); return Task.FromResult(probe); }); throw new Exception("Restart during activation accepted"); }
catch (InvalidOperationException) { if (!probe.Disposed) throw new Exception("Failed activation leaked capture"); }
Console.WriteLine("PASS missing identity, activation failure and restart-during-activation cleanup");
var buffered = new BufferedWaveProvider(new WaveFormat(16000,16,1), TimeSpan.FromSeconds(2));
try { buffered.AddSamples(new byte[64002]); throw new Exception("Overflow not bounded"); } catch (InvalidOperationException) { }
if (buffered.BufferedBytes > 64000) throw new Exception("Unbounded native buffer");
Console.WriteLine("PASS native buffer limit");
sealed class DisposableProbe : IDisposable { public bool Disposed; public void Dispose() => Disposed = true; }
