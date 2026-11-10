require('dotenv').config();
const fs = require('fs');
const http = require('http');
const path = require('path');
const { spawn } = require('child_process');
const { Client, GatewayIntentBits } = require('discord.js');
const {
  joinVoiceChannel,
  getVoiceConnection,
  EndBehaviorType,
  VoiceConnectionStatus,
  entersState,
} = require('@discordjs/voice');
const prism = require('prism-media');
const WebSocket = require('ws');

// ---------------------------------------------------------------------------
// Config
// ---------------------------------------------------------------------------
const DISCORD_TOKEN = process.env.DISCORD_TOKEN;
const AUTO_JOIN = process.env.AUTO_JOIN === 'true';
const WHISPER_MODEL = path.resolve(process.env.WHISPER_MODEL || './whisper/ggml-base.en.bin');

// GPU support lives in DLLs next to whisper-server.exe (ggml-cuda.dll for
// NVIDIA, ggml-vulkan.dll for AMD/Intel). The CUDA zip unpacks into a
// "Release" subfolder, so an old CPU exe at ./whisper/whisper-server.exe is
// easy to keep using by mistake. Prefer any whisper-server.exe that actually
// has a GPU DLL beside it.
const GPU_DLLS = ['ggml-cuda.dll', 'ggml-vulkan.dll'];

function gpuDllIn(dir) {
  return GPU_DLLS.find((d) => fs.existsSync(path.join(dir, d))) || null;
}

function findServerExes(dir, depth = 0, out = []) {
  if (depth > 3 || !fs.existsSync(dir)) return out;
  for (const e of fs.readdirSync(dir, { withFileTypes: true })) {
    const full = path.join(dir, e.name);
    if (e.isDirectory()) findServerExes(full, depth + 1, out);
    else if (e.name.toLowerCase() === 'whisper-server.exe') out.push(full);
  }
  return out;
}

function resolveServerExe() {
  const configured = path.resolve(process.env.WHISPER_SERVER_EXE || './whisper/whisper-server.exe');
  if (process.env.WHISPER_GPU === 'false') return configured;
  if (fs.existsSync(configured) && gpuDllIn(path.dirname(configured))) return configured;
  const withGpu = findServerExes(path.resolve('./whisper')).find((f) => gpuDllIn(path.dirname(f)));
  if (withGpu) {
    console.log(`🔎 Using GPU build found at: ${withGpu}`);
    return withGpu;
  }
  return configured;
}

const WHISPER_SERVER_EXE = resolveServerExe();
const WHISPER_DIR = path.dirname(WHISPER_SERVER_EXE);
const GPU_DLL = gpuDllIn(WHISPER_DIR);
const WHISPER_PORT = Number(process.env.WHISPER_PORT || 8178);
const WHISPER_THREADS = process.env.WHISPER_THREADS || '6';
const USE_GPU = process.env.WHISPER_GPU !== 'false';
const LANGUAGE = process.env.WHISPER_LANGUAGE || 'en';

const REFRESH_MS = Number(process.env.REFRESH_MS || 400);
const MIN_AUDIO_MS = Number(process.env.MIN_AUDIO_MS || 900);
const MAX_SEGMENT_MS = Number(process.env.MAX_SEGMENT_MS || 12000);

// How many transcriptions may run at once. With several people talking,
// this keeps the GPU from being swamped (which would hurt game FPS).
const MAX_CONCURRENT = Number(process.env.MAX_CONCURRENT || 2);

// audio_ctx: the single biggest speed lever. Whisper normally processes a
// full 30s window no matter how short your clip is. Shrinking the audio
// context to match our real segment length gives ~3x speedup with little
// accuracy loss. Formula from the whisper.cpp maintainers:
//   (clip_seconds / 30) * 1500 + 128
const AUDIO_CTX = Number(
  process.env.AUDIO_CTX || Math.min(1500, Math.round((MAX_SEGMENT_MS / 1000 / 30) * 1500 + 128))
);

// Speed flags. Set any to false in .env if your whisper build rejects them.
// Flash attention crashes on some Vulkan drivers and silently drops to CPU,
// so it's only on by default for the CUDA build.
const USE_FLASH_ATTN =
  process.env.FLASH_ATTN === 'true' ||
  (process.env.FLASH_ATTN !== 'false' && GPU_DLL !== 'ggml-vulkan.dll');
const NO_FALLBACK = process.env.NO_FALLBACK !== 'false';

// Skip transcribing near-silence (keyboard-only moments, breathing).
// Lower = more sensitive. 0 disables the gate.
const SILENCE_RMS = Number(process.env.SILENCE_RMS || 350);

const BYTES_PER_MS = 32;

if (!DISCORD_TOKEN) {
  console.error('Missing DISCORD_TOKEN in .env');
  process.exit(1);
}
for (const [label, p] of [['whisper-server', WHISPER_SERVER_EXE], ['model', WHISPER_MODEL]]) {
  if (!fs.existsSync(p)) {
    console.error(`❌ ${label} not found at: ${p}`);
    process.exit(1);
  }
}

// ---------------------------------------------------------------------------
// whisper-server (model stays loaded in VRAM)
// ---------------------------------------------------------------------------
let whisperReady = false;

function startWhisperServer() {
  const args = [
    '-m', WHISPER_MODEL,
    '--host', '127.0.0.1',
    '--port', String(WHISPER_PORT),
    '-t', WHISPER_THREADS,
    '-ac', String(AUDIO_CTX),   // audio context — the big speed win
    '-bs', '1',                 // greedy decoding, no beam search
    '-bo', '1',
    '-l', LANGUAGE,
  ];
  if (USE_FLASH_ATTN) args.push('-fa');   // flash attention (big on RTX cards)
  if (NO_FALLBACK) args.push('-nf');      // don't re-decode at higher temps
  if (!USE_GPU) args.push('-ng');

  console.log(`🧠 whisper-server: ${args.join(' ')}`);
  console.log(`   exe: ${WHISPER_SERVER_EXE}`);
  console.log(`   GPU DLL beside exe: ${GPU_DLL || 'NONE — this is a CPU-only build'}`);

  // Run from the exe's own folder with that folder first on PATH, so Windows
  // finds ggml-cuda.dll and its cuBLAS/cudart DLLs no matter where we start.
  const env = { ...process.env, PATH: WHISPER_DIR + path.delimiter + (process.env.PATH || '') };
  const proc = spawn(WHISPER_SERVER_EXE, args, { windowsHide: true, cwd: WHISPER_DIR, env });

  let startupLog = '';
  const watch = (data) => {
    const s = data.toString();
    startupLog += s;
    for (const line of s.split(/\r?\n/)) {
      if (/ggml_cuda|ggml_vulkan|load_backend|use gpu|no gpu|cuda error|vulkan error|failed to (load|init)|devices\s*=|backends\s*=/i.test(line)) {
        console.log(`   [whisper] ${line.trim()}`);
      }
    }
    if (/listening|running|server started/i.test(s) && !whisperReady) {
      whisperReady = true;
      console.log('✅ whisper-server ready — model loaded.');
      reportBackend(startupLog);
    }
    if (/unknown argument|invalid argument|unrecognized/i.test(s)) {
      console.error('');
      console.error('❌ whisper-server rejected a flag. Your build may be older.');
      console.error('   Add these to .env to disable the newer speed flags:');
      console.error('     FLASH_ATTN=false');
      console.error('     NO_FALLBACK=false');
      console.error('');
      process.stderr.write(s);
    }
  };
  proc.stdout.on('data', watch);
  proc.stderr.on('data', watch);

  proc.on('error', (e) => {
    console.error(`❌ Could not start whisper-server: ${e.message}`);
    process.exit(1);
  });
  proc.on('close', (code) => {
    whisperReady = false;
    console.error(`❌ whisper-server exited (code ${code}).`);
    if (code !== 0) console.error(startupLog.split('\n').slice(-8).join('\n'));
  });

  setTimeout(() => {
    if (!whisperReady) {
      whisperReady = true;
      console.log('✅ whisper-server assumed ready.');
      reportBackend(startupLog);
    }
  }, 10000);

  return proc;
}

// whisper-server prints which backend it loaded. WHISPER_GPU=true only
// *allows* GPU — if the exe is the plain CPU build, it silently runs on CPU.
function reportBackend(log) {
  const dev = log.match(/Device 0:\s*([^,\r\n]+)/);
  const gpuLoaded = /loaded (CUDA|Vulkan|SYCL|Metal) backend|ggml_cuda_init: found [1-9]|ggml_vulkan: Found [1-9]/i.test(log);
  if (gpuLoaded) {
    console.log(`🚀 GPU backend active${dev ? `: ${dev[1].trim()}` : ''}`);
  } else if (USE_GPU) {
    console.error('');
    console.error('❌ Running on CPU even though WHISPER_GPU=true.');
    if (!GPU_DLL) {
      console.error('   No ggml-cuda.dll / ggml-vulkan.dll next to the exe = CPU-only build.');
      console.error('   Run GPU_CHECK.bat and follow what it says.');
    } else {
      console.error(`   ${GPU_DLL} is present but did not load. See the [whisper] lines above.`);
      console.error('   Most common: outdated NVIDIA driver, or a missing cublas/cudart DLL.');
      console.error('   Run GPU_CHECK.bat and send gpu_check.txt.');
    }
    console.error('');
  }
}

const whisperProc = startWhisperServer();

// ---------------------------------------------------------------------------
// Perf tracking
// ---------------------------------------------------------------------------
const perf = { count: 0, totalMs: 0, worstMs: 0, skipped: 0 };
let warnedSlow = false;

function perfSummary() {
  if (!perf.count) return 'No transcriptions yet.';
  const avg = perf.totalMs / perf.count;
  return (
    `runs: ${perf.count} | avg: ${avg.toFixed(0)}ms | worst: ${perf.worstMs}ms | ` +
    `silence-skipped: ${perf.skipped} | model: ${path.basename(WHISPER_MODEL)} | ` +
    `${USE_GPU ? 'GPU' : 'CPU'} | audio_ctx: ${AUDIO_CTX}`
  );
}

// ---------------------------------------------------------------------------
// Audio helpers
// ---------------------------------------------------------------------------
function wavBuffer(pcm) {
  const h = Buffer.alloc(44);
  const d = pcm.length;
  h.write('RIFF', 0); h.writeUInt32LE(36 + d, 4); h.write('WAVE', 8);
  h.write('fmt ', 12); h.writeUInt32LE(16, 16); h.writeUInt16LE(1, 20);
  h.writeUInt16LE(1, 22); h.writeUInt32LE(16000, 24); h.writeUInt32LE(32000, 28);
  h.writeUInt16LE(2, 32); h.writeUInt16LE(16, 34);
  h.write('data', 36); h.writeUInt32LE(d, 40);
  return Buffer.concat([h, pcm]);
}

// Cheap loudness check so we don't burn a full inference pass on silence.
function rms(pcm) {
  const n = Math.floor(pcm.length / 2);
  if (!n) return 0;
  const step = Math.max(1, Math.floor(n / 2000)); // sample, don't scan it all
  let sum = 0, c = 0;
  for (let i = 0; i < n; i += step) {
    const v = pcm.readInt16LE(i * 2);
    sum += v * v;
    c++;
  }
  return Math.sqrt(sum / c);
}

function inference(pcm) {
  const started = Date.now();
  return new Promise((resolve, reject) => {
    const boundary = '----cap' + Math.random().toString(36).slice(2);
    const field = (n, v) =>
      Buffer.from(`--${boundary}\r\nContent-Disposition: form-data; name="${n}"\r\n\r\n${v}\r\n`);

    const body = Buffer.concat([
      Buffer.from(
        `--${boundary}\r\nContent-Disposition: form-data; name="file"; filename="a.wav"\r\n` +
        `Content-Type: audio/wav\r\n\r\n`
      ),
      wavBuffer(pcm),
      Buffer.from('\r\n'),
      field('temperature', '0.0'),
      field('response_format', 'json'),
      field('language', LANGUAGE),
      field('audio_ctx', String(AUDIO_CTX)),
      Buffer.from(`--${boundary}--\r\n`),
    ]);

    const req = http.request(
      {
        host: '127.0.0.1', port: WHISPER_PORT, path: '/inference', method: 'POST',
        headers: {
          'Content-Type': `multipart/form-data; boundary=${boundary}`,
          'Content-Length': body.length,
        },
        timeout: 30000,
      },
      (res) => {
        let out = '';
        res.on('data', (d) => (out += d));
        res.on('end', () => {
          const ms = Date.now() - started;
          perf.count++; perf.totalMs += ms;
          if (ms > perf.worstMs) perf.worstMs = ms;
          if (process.env.SHOW_TIMING === 'true') {
            console.log(`   ⏱  ${ms}ms (${(pcm.length / BYTES_PER_MS / 1000).toFixed(1)}s audio)`);
          }
          if (!warnedSlow && perf.count >= 5 && perf.totalMs / perf.count > 1500) {
            warnedSlow = true;
            console.log('\n⚠️  Averaging over 1.5s per pass — captions will feel behind.');
            console.log('   Try a smaller model, or lower MAX_SEGMENT_MS (lowers audio_ctx too).\n');
          }
          try {
            resolve((JSON.parse(out).text || '').trim());
          } catch {
            resolve(out.trim());
          }
        });
      }
    );
    req.on('error', reject);
    req.on('timeout', () => req.destroy(new Error('timeout')));
    req.end(body);
  });
}

const NOISE = /^[\s\.\?!,]*(\(|\[|\*)?\s*(blank_audio|inaudible|silence|music|no audio|sound of|clicking|typing)\s*(\)|\]|\*)?[\s\.\?!,]*$/i;

// Whisper often loops on short clips: "Welcome back. Welcome back."
// Drop a sentence that repeats the one right before it, and collapse
// repeated 2–6 word phrases ("you know you know"). Single-word repeats
// ("no no no") are left alone since people really say those.
const normSentence = (x) => x.toLowerCase().replace(/[^\p{L}\p{N}' ]/gu, '').trim();

function dedupe(t) {
  const out = [];
  for (const sentence of t.split(/(?<=[.!?])\s+/)) {
    const n = normSentence(sentence);
    if (n && out.length && normSentence(out[out.length - 1]) === n) continue;
    out.push(sentence);
  }
  return out
    .join(' ')
    .replace(/\b((?:[\p{L}\p{N}']+\s+){1,5}[\p{L}\p{N}']+)(?:[,.!?]?\s+\1\b)+/giu, '$1');
}

function clean(text) {
  const t = dedupe(text.replace(/\[[^\]]*\]/g, '').replace(/\s+/g, ' ').trim());
  return !t || NOISE.test(t) ? '' : t;
}

// ---------------------------------------------------------------------------
// Fair scheduler — with several people talking, everyone gets served.
// Priority goes to whoever has the most un-transcribed audio waiting.
// ---------------------------------------------------------------------------
const pending = new Set();
let running = 0;

function requestSlot(caption) {
  pending.add(caption);
  pump();
}

function pump() {
  while (running < MAX_CONCURRENT && pending.size > 0) {
    let best = null;
    for (const c of pending) {
      if (!best || c.newBytes() > best.newBytes()) best = c;
    }
    pending.delete(best);
    running++;
    best.run().finally(() => {
      running--;
      pump();
    });
  }
}

// ---------------------------------------------------------------------------
// One live, self-updating caption line per speaker
// ---------------------------------------------------------------------------
class LiveCaption {
  constructor(userId, name, avatar) {
    this.userId = userId;
    this.name = name;
    this.avatar = avatar;
    this.buf = Buffer.alloc(0);
    this.lastSentBytes = 0;
    this.lastText = '';
    this.segmentStart = Date.now();
    this.stopped = false;
    this.busy = false;
    this.delay = REFRESH_MS;
    this.gen = 0; // bumps on every commit; stale in-flight results are dropped
    this.schedule();
  }

  newBytes() {
    return this.buf.length - this.lastSentBytes;
  }

  schedule() {
    if (this.stopped) return;
    this.timer = setTimeout(() => this.tick(), this.delay);
  }

  push(mono) {
    this.buf = Buffer.concat([this.buf, mono]);
  }

  tick() {
    if (this.stopped) return;
    if (this.busy || !whisperReady) return this.schedule();
    if (this.buf.length < MIN_AUDIO_MS * BYTES_PER_MS) return this.schedule();

    // Nothing new since last pass — don't redo identical work.
    if (this.newBytes() < 150 * BYTES_PER_MS) return this.schedule();

    if (Date.now() - this.segmentStart > MAX_SEGMENT_MS) {
      this.commit().then(() => this.schedule());
      return;
    }

    this.busy = true;
    requestSlot(this);
  }

  async run() {
    const snapshot = this.buf;
    const gen = this.gen;
    const started = Date.now();
    try {
      if (SILENCE_RMS > 0 && rms(snapshot) < SILENCE_RMS) {
        perf.skipped++;
      } else {
        const text = clean(await inference(snapshot));
        // If the sentence was finalised while we were waiting, this partial is
        // stale — sending it would start a new line with the same words.
        if (gen !== this.gen || this.stopped) return;
        if (text && text !== this.lastText && !this.discarded) {
          this.lastText = text;
          sendCaption({ speaker: this.name, speakerId: this.userId, avatar: this.avatar, text, final: false });
        }
      }
      if (gen === this.gen) this.lastSentBytes = snapshot.length;
    } catch (e) {
      console.error(`⚠️  inference failed (${this.name}): ${e.message}`);
    } finally {
      this.busy = false;
      const took = Date.now() - started;
      this.delay = Math.max(REFRESH_MS, Math.round(took * 0.3));
      this.schedule();
    }
  }

  async commit() {
    const audio = this.buf;
    this.gen++; // invalidate any partial still in flight
    this.buf = Buffer.alloc(0);
    this.lastSentBytes = 0;
    this.lastText = '';
    this.segmentStart = Date.now();

    if (audio.length >= MIN_AUDIO_MS * BYTES_PER_MS && whisperReady) {
      try {
        if (!(SILENCE_RMS > 0 && rms(audio) < SILENCE_RMS)) {
          const text = clean(await inference(audio));
          if (text && !this.discarded) {
            console.log(`💬 ${this.name}: ${text}`);
            sendCaption({ speaker: this.name, speakerId: this.userId, avatar: this.avatar, text, final: true });
          }
        }
      } catch (e) {
        console.error(`⚠️  final failed (${this.name}): ${e.message}`);
      }
    }
  }

  async destroy() {
    this.stopped = true;
    clearTimeout(this.timer);
    pending.delete(this);
    if (!this.discarded) await this.commit();
  }

  // Throw away everything without captioning it (used when someone is muted).
  discard() {
    this.discarded = true;
    this.stopped = true;
    clearTimeout(this.timer);
    pending.delete(this);
    this.buf = Buffer.alloc(0);
  }
}

// ---------------------------------------------------------------------------
// Caption mute list — muted people are never transcribed. Saved to
// muted.json so it survives restarts. This does NOT server-mute anyone in
// Discord; it only stops the bot from captioning them.
// ---------------------------------------------------------------------------
const MUTE_FILE = path.join(__dirname, 'muted.json');
const muted = new Map(); // userId -> display name

try {
  const saved = JSON.parse(fs.readFileSync(MUTE_FILE, 'utf8'));
  for (const [id, name] of Object.entries(saved)) muted.set(id, name);
  if (muted.size) console.log(`🔇 Loaded ${muted.size} caption-muted user(s).`);
} catch {}

function saveMuted() {
  try {
    fs.writeFileSync(MUTE_FILE, JSON.stringify(Object.fromEntries(muted), null, 2));
  } catch (e) {
    console.error(`⚠️  Could not save muted.json: ${e.message}`);
  }
}

// ---------------------------------------------------------------------------
// Overlay
// ---------------------------------------------------------------------------
const overlayServer = new WebSocket.Server({ port: 8080 });
const overlayClients = new Set();

overlayServer.on('connection', (ws) => {
  overlayClients.add(ws);
  console.log(`🖥️  Overlay connected (${overlayClients.size}).`);
  ws.on('close', () => overlayClients.delete(ws));
});

function sendCaption(payload) {
  const msg = JSON.stringify(payload);
  for (const c of overlayClients) if (c.readyState === WebSocket.OPEN) c.send(msg);
}

// ---------------------------------------------------------------------------
// 48kHz stereo -> 16kHz mono
// ---------------------------------------------------------------------------
function makeDownsampler() {
  let leftover = Buffer.alloc(0);
  return function (chunk) {
    const data = Buffer.concat([leftover, chunk]);
    const frames = Math.floor(data.length / 4);
    leftover = data.subarray(frames * 4);
    const n = Math.floor(frames / 3);
    const mono = Buffer.alloc(n * 2);
    for (let i = 0; i < n; i++) {
      let sum = 0;
      for (let j = 0; j < 3; j++) {
        const off = (i * 3 + j) * 4;
        sum += (data.readInt16LE(off) + data.readInt16LE(off + 2)) / 2;
      }
      let v = Math.round(sum / 3);
      if (v > 32767) v = 32767;
      if (v < -32768) v = -32768;
      mono.writeInt16LE(v, i * 2);
    }
    return mono;
  };
}

// ---------------------------------------------------------------------------
// Discord
// ---------------------------------------------------------------------------
const client = new Client({
  intents: [
    GatewayIntentBits.Guilds,
    GatewayIntentBits.GuildVoiceStates,
    GatewayIntentBits.GuildMessages,
    GatewayIntentBits.MessageContent,
    GatewayIntentBits.GuildMembers,
  ],
});

// ---------------------------------------------------------------------------
// Command names — all changeable from .env. Matching is case-insensitive.
// ---------------------------------------------------------------------------
const PREFIX = (process.env.PREFIX || '!').trim();
const cmdName = (key, fallback) =>
  (process.env[key] || fallback).trim().toLowerCase().replace(/^[!./$?]+/, '');

const CMD = {
  join:     cmdName('CMD_JOIN',     'comehachi'),
  leave:    cmdName('CMD_LEAVE',    'byehachi'),
  mute:     cmdName('CMD_MUTE',     'mute'),
  unmute:   cmdName('CMD_UNMUTE',   'unmute'),
  mutelist: cmdName('CMD_MUTELIST', 'mutelist'),
  stats:    cmdName('CMD_STATS',    'stats'),
  help:     cmdName('CMD_HELP',     'hachibot'),
};
const c = (name) => `${PREFIX}${CMD[name]}`; // display form, e.g. "!comehachi"

// Only these Discord user IDs can use commands. Override in .env with
// ALLOWED_USERS=id1,id2,...
const ALLOWED_USERS = new Set(
  (process.env.ALLOWED_USERS || '205073814101426176,777188566592913408')
    .split(',').map((x) => x.trim()).filter(Boolean)
);
const DENY_MESSAGE = process.env.DENY_MESSAGE || 'bork bork! You are not mama!! 🐶';

{
  const seen = new Map();
  for (const [k, v] of Object.entries(CMD)) {
    if (seen.has(v)) console.error(`⚠️  CMD_${seen.get(v).toUpperCase()} and CMD_${k.toUpperCase()} are both "${v}" — fix your .env`);
    seen.set(v, k);
  }
}

client.once('ready', () => {
  console.log(`Logged in as ${client.user.tag}`);
  console.log(
    `⚙️  ${USE_GPU ? 'GPU' : 'CPU'} | ${path.basename(WHISPER_MODEL)} | ` +
    `threads ${WHISPER_THREADS} | refresh ${REFRESH_MS}ms | audio_ctx ${AUDIO_CTX} | ` +
    `max ${MAX_CONCURRENT} at once`
  );
  console.log('Overlay: ws://localhost:8080 — open overlay.html');
  console.log(AUTO_JOIN ? 'AUTO_JOIN is ON.' : `Type ${c('join')} to start.`);
  console.log(`Commands: ${Object.keys(CMD).map(c).join('  ')}`);
  console.log(`Allowed users: ${[...ALLOWED_USERS].join(', ')}`);
});

const active = new Map(); // userId -> { live, opusStream }

function stopSpeaker(userId) {
  const a = active.get(userId);
  if (!a) return;
  active.delete(userId);
  if (a.live) a.live.discard();
  if (a.opusStream) a.opusStream.destroy();
}

function listenToChannel(connection, guild) {
  connection.receiver.speaking.on('start', async (userId) => {
    if (muted.has(userId) || active.has(userId)) return;
    const slot = {};
    active.set(userId, slot);

    const member = await guild.members.fetch(userId).catch(() => null);
    const name = member?.displayName || `User ${userId}`;
    const avatar = member?.displayAvatarURL({ extension: 'png', size: 128 }) || null;

    const opusStream = connection.receiver.subscribe(userId, {
      end: { behavior: EndBehaviorType.AfterSilence, duration: 800 },
    });
    const pcmStream = opusStream.pipe(
      new prism.opus.Decoder({ rate: 48000, channels: 2, frameSize: 960 })
    );

    // Muted while we were fetching their name — bail out.
    if (muted.has(userId) || active.get(userId) !== slot) {
      opusStream.destroy();
      return;
    }

    const live = new LiveCaption(userId, name, avatar);
    const downsample = makeDownsampler();
    slot.live = live;
    slot.opusStream = opusStream;

    pcmStream.on('data', (chunk) => live.push(downsample(chunk)));
    pcmStream.on('error', (e) => console.error(`Decode error (${name}):`, e.message));

    const finish = async () => {
      if (active.get(userId) === slot) active.delete(userId);
      await live.destroy();
    };
    opusStream.once('end', finish);
    opusStream.once('close', finish);
  });
}

function startListening(vc) {
  const connection = joinVoiceChannel({
    channelId: vc.id,
    guildId: vc.guild.id,
    adapterCreator: vc.guild.voiceAdapterCreator,
    selfDeaf: false,
  });
  connection.on(VoiceConnectionStatus.Ready, () => console.log(`✅ Listening in "${vc.name}".`));
  connection.on('error', (e) => console.error('Voice error:', e.message));
  entersState(connection, VoiceConnectionStatus.Ready, 15_000).catch(() =>
    console.error('❌ Timed out joining voice.')
  );
  listenToChannel(connection, vc.guild);
  return connection;
}

client.on('voiceStateUpdate', (oldS, newS) => {
  if (!AUTO_JOIN || newS.member?.user?.bot) return;
  const joined = newS.channel;
  if (!joined || oldS.channelId === newS.channelId) return;
  if (getVoiceConnection(newS.guild.id)) return;
  console.log(`👋 Auto-joining "${joined.name}".`);
  startListening(joined);
});

client.on('messageCreate', async (message) => {
  if (message.author.bot || !message.guild) return;

  const text = message.content.trim();
  if (!text.startsWith(PREFIX)) return;
  const [rawCmd, ...args] = text.slice(PREFIX.length).split(/\s+/);
  const cmd = rawCmd.toLowerCase();

  // Not one of our commands (e.g. another bot's) — stay quiet.
  if (!Object.values(CMD).includes(cmd)) return;

  if (!ALLOWED_USERS.has(message.author.id)) {
    console.log(`🚫 ${message.author.username} (${message.author.id}) tried ${PREFIX}${cmd}`);
    return void message.reply(DENY_MESSAGE);
  }

  if (cmd === CMD.join) {
    const vc = message.member?.voice?.channel;
    if (!vc) return void message.reply('Join a voice channel first.');
    startListening(vc);
    message.reply(`bork bork! mama!! 🐶`);
    return;
  }

  if (cmd === CMD.leave) {
    const conn = getVoiceConnection(message.guild.id);
    if (conn) { conn.destroy(); for (const id of [...active.keys()]) stopSpeaker(id); message.reply('escape. 👋'); }
    else message.reply("I'm not in a voice channel.");
    return;
  }

  if (cmd === CMD.stats) {
    const s = perfSummary();
    console.log(`📊 ${s}`);
    message.reply('```' + s + '```');
    return;
  }

  if (cmd === CMD.mute || cmd === CMD.unmute) {
    // "!mute @Shator", "!mute 2738...", or just "!mute" to mute yourself
    const target =
      message.mentions.users.first() ||
      (args[0] && /^\d{15,21}$/.test(args[0])
        ? await client.users.fetch(args[0]).catch(() => null)
        : null) ||
      (args.length === 0 ? message.author : null);

    if (!target) return void message.reply(`Usage: \`${c('mute')} @user\` / \`${c('unmute')} @user\` (or no @ for yourself).`);
    if (target.bot) return void message.reply("Bots aren't captioned anyway.");

    const member = await message.guild.members.fetch(target.id).catch(() => null);
    const name = member?.displayName || target.username;

    if (cmd === CMD.mute) {
      if (muted.has(target.id)) return void message.reply(`**${name}** is already muted.`);
      muted.set(target.id, name);
      saveMuted();
      stopSpeaker(target.id);
      sendCaption({ type: 'remove', speakerId: target.id });
      console.log(`🔇 ${name} caption-muted by ${message.author.username}.`);
      message.reply(`🔇 **${name}** won't be captioned. \`${c('unmute')}\` to undo.`);
    } else {
      if (!muted.delete(target.id)) return void message.reply(`**${name}** isn't muted.`);
      saveMuted();
      console.log(`🔊 ${name} caption-unmuted by ${message.author.username}.`);
      message.reply(`🔊 **${name}** will be captioned again.`);
    }
    return;
  }

  if (cmd === CMD.mutelist) {
    if (!muted.size) return void message.reply('Nobody is muted.');
    const lines = [...muted].map(([id, name]) => `• **${name}** (<@${id}>)`);
    message.reply({
      content: `🔇 Caption-muted (${muted.size}):\n${lines.join('\n')}`,
      allowedMentions: { parse: [] }, // show the tags without pinging anyone
    });
    return;
  }

  if (cmd === CMD.help) {
    message.reply([
      '**Hachi — live captions**',
      `\`${c('join')}\` — join your voice channel and start captions`,
      `\`${c('leave')}\` — leave the voice channel`,
      `\`${c('mute')} [@user]\` — stop captioning someone (no @ = you)`,
      `\`${c('unmute')} [@user]\` — caption them again`,
      `\`${c('mutelist')}\` — who's muted`,
      `\`${c('stats')}\` — speed stats`,
    ].join('\n'));
  }
});

process.on('unhandledRejection', (e) => console.error('❌ Unhandled:', e));
process.on('SIGINT', () => {
  try { whisperProc.kill(); } catch {}
  process.exit(0);
});

client.login(DISCORD_TOKEN);