require('dotenv').config();
const { Client, GatewayIntentBits } = require('discord.js');
const { joinVoiceChannel, getVoiceConnection, EndBehaviorType, VoiceConnectionStatus, entersState } = require('@discordjs/voice');
const prism = require('prism-media');
const WebSocket = require('ws');

// ---------------------------------------------------------------------------
// Startup self-check. The DAVE Protocol section matters most: Discord now
// requires DAVE (end-to-end encryption) for normal voice channels, and if
// @snazzah/davey is missing, Discord rejects the connection with close code
// 4017 and the connection silently times out with no error.
// ---------------------------------------------------------------------------
const { generateDependencyReport } = require('@discordjs/voice');
console.log(generateDependencyReport());

const DISCORD_TOKEN = process.env.DISCORD_TOKEN;
const ASSEMBLYAI_API_KEY = process.env.ASSEMBLYAI_API_KEY;

if (!DISCORD_TOKEN || !ASSEMBLYAI_API_KEY) {
  console.error('Missing DISCORD_TOKEN or ASSEMBLYAI_API_KEY. Check your .env file.');
  process.exit(1);
}

// ---------------------------------------------------------------------------
// Local WebSocket server. The overlay.html page connects to this to receive
// live caption events. Nothing here leaves your own computer.
// ---------------------------------------------------------------------------
const overlayServer = new WebSocket.Server({ port: 8080 });
const overlayClients = new Set();

overlayServer.on('connection', (ws) => {
  overlayClients.add(ws);
  ws.on('close', () => overlayClients.delete(ws));
});

function sendCaption(payload) {
  const message = JSON.stringify(payload);
  for (const client of overlayClients) {
    if (client.readyState === WebSocket.OPEN) client.send(message);
  }
}

// ---------------------------------------------------------------------------
// Discord bot setup
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

client.once('ready', () => {
  console.log(`Logged in as ${client.user.tag}`);
  console.log('Caption server running at ws://localhost:8080 — open overlay.html to view captions.');
  if (AUTO_JOIN) {
    console.log('AUTO_JOIN is ON — the bot will follow you into any voice channel automatically.');
  } else {
    console.log('In a Discord text channel, join a voice channel and type !join to start.');
    console.log('(Tip: add AUTO_JOIN=true to your .env to skip the command entirely.)');
  }
});

// Set AUTO_JOIN=true in your .env to make the bot automatically follow
// whoever joins a voice channel — no !join command needed. This is the
// easiest mode if the Message Content Intent is giving you trouble.
const AUTO_JOIN = process.env.AUTO_JOIN === 'true';

function startListening(voiceChannel) {
  const connection = joinVoiceChannel({
    channelId: voiceChannel.id,
    guildId: voiceChannel.guild.id,
    adapterCreator: voiceChannel.guild.voiceAdapterCreator,
    selfDeaf: false, // must be false, otherwise Discord sends us no audio at all
  });

  connection.on('stateChange', (oldState, newState) => {
    console.log(`   🔄 voice state: ${oldState.status} -> ${newState.status}`);
  });

  connection.on(VoiceConnectionStatus.Ready, () => {
    console.log(`✅ Voice connection is READY in "${voiceChannel.name}" — should now hear speech.`);
  });
  connection.on(VoiceConnectionStatus.Disconnected, () => {
    console.log(`⚠️  Voice connection DISCONNECTED from "${voiceChannel.name}".`);
  });
  connection.on('error', (err) => {
    console.error('Voice connection error:', err.message);
  });

  // If we never reach Ready within 15 seconds, say so loudly instead of
  // hanging silently forever.
  entersState(connection, VoiceConnectionStatus.Ready, 15_000).catch(() => {
    console.error('❌ TIMED OUT waiting for voice connection (stuck at: ' + connection.state.status + ')');
    console.error('   Check the DAVE Protocol line in the dependency report above.');
    console.error('   If @snazzah/davey is "not found", run: npm install @snazzah/davey');
  });

  // Discord closes with 4017 when the client cannot speak the DAVE
  // end-to-end-encryption protocol. The library does not surface this, so
  // we log it ourselves.
  connection.on('debug', (msg) => {
    if (msg.includes('4017') || msg.toLowerCase().includes('dave')) {
      console.log(`   🔐 DAVE/close-code info: ${msg}`);
    }
  });

  listenToChannel(connection, voiceChannel.guild);
  return connection;
}

// Auto-join: whenever a human joins a voice channel and the bot isn't
// already in one on that server, the bot follows them in.
client.on('voiceStateUpdate', (oldState, newState) => {
  if (!AUTO_JOIN) return;
  if (newState.member?.user?.bot) return;

  const joinedChannel = newState.channel;
  if (!joinedChannel || oldState.channelId === newState.channelId) return;

  const existing = getVoiceConnection(newState.guild.id);
  if (existing) return;

  console.log(`👋 ${newState.member.displayName} joined "${joinedChannel.name}" — auto-joining.`);
  startListening(joinedChannel);
});

client.on('messageCreate', async (message) => {
  if (message.author.bot) return;

  // Diagnostic: if content shows as EMPTY here, the Message Content Intent
  // is not enabled in the Discord Developer Portal.
  console.log(`💬 Message from ${message.author.username}: "${message.content}" ${message.content ? '' : '<-- EMPTY! Message Content Intent is OFF'}`);

  if (message.content === '!join') {
    const voiceChannel = message.member?.voice?.channel;
    console.log(`   voice channel detected: ${voiceChannel ? `"${voiceChannel.name}"` : 'NONE — are you actually in a voice channel?'}`);

    if (!voiceChannel) {
      message.reply('Join a voice channel first, then type !join again.');
      return;
    }

    const perms = voiceChannel.permissionsFor(message.guild.members.me);
    console.log(`   bot permissions here — View: ${perms?.has('ViewChannel')}, Connect: ${perms?.has('Connect')}`);

    try {
      startListening(voiceChannel);
      message.reply(`Listening in "${voiceChannel.name}". Open overlay.html to see captions.`);
    } catch (err) {
      console.error('❌ Failed to join voice channel:', err);
      message.reply(`Couldn't join: ${err.message}`);
    }
    return;
  }

  if (message.content === '!leave') {
    const connection = getVoiceConnection(message.guild.id);
    if (connection) {
      connection.destroy();
      message.reply('Left the voice channel.');
    } else {
      message.reply("I'm not in a voice channel here.");
    }
  }
});

// ---------------------------------------------------------------------------
// The core pipeline: one Opus stream + one Deepgram connection per speaker,
// opened the moment they start talking and closed when they go quiet.
// ---------------------------------------------------------------------------
const activeStreams = new Map(); // userId -> deepgram socket, so we don't double-subscribe

function listenToChannel(connection, guild) {
  const receiver = connection.receiver;

  receiver.speaking.on('start', async (userId) => {
    if (activeStreams.has(userId)) return; // this user is already being transcribed

    const member = await guild.members.fetch(userId).catch(() => null);
    const speakerName = member?.displayName || `User ${userId}`;
    console.log(`🎙️  ${speakerName} started talking — opening a transcription stream...`);

    // Discord gives us this user's audio as its own Opus packet stream,
    // separate from everyone else's — that's what makes per-speaker captions possible.
    const opusStream = receiver.subscribe(userId, {
      end: { behavior: EndBehaviorType.AfterSilence, duration: 1000 },
    });

    // Discord always sends 48kHz stereo Opus. Decode at that native rate —
    // asking the decoder for 16kHz here does NOT reliably resample, which
    // makes us send 6x more data than we claim and trips AssemblyAI's
    // rate limit (close code 3007).
    const pcmStream = opusStream.pipe(
      new prism.opus.Decoder({ rate: 48000, channels: 2, frameSize: 960 })
    );

    const sttSocket = new WebSocket(
      'wss://streaming.assemblyai.com/v3/ws?sample_rate=16000&encoding=pcm_s16le&speech_model=universal-streaming-english&format_turns=true',
      { headers: { Authorization: ASSEMBLYAI_API_KEY } }
    );

    activeStreams.set(userId, sttSocket);

    // Buffer of outgoing 16kHz mono audio, flushed in ~100ms chunks.
    // AssemblyAI rejects messages outside the 50ms-1000ms range.
    let outBuffer = Buffer.alloc(0);
    let leftover = Buffer.alloc(0);
    const CHUNK_BYTES = 3200; // 100ms @ 16kHz, 16-bit mono

    sttSocket.on('open', () => {
      console.log(`🔌 Connected to AssemblyAI for ${speakerName}.`);

      pcmStream.on('data', (chunk) => {
        if (sttSocket.readyState !== WebSocket.OPEN) return;

        // Carry over any partial frame from last time.
        const data = Buffer.concat([leftover, chunk]);
        const frameCount = Math.floor(data.length / 4); // 4 bytes per stereo frame
        leftover = data.subarray(frameCount * 4);

        // 48kHz stereo -> 16kHz mono. Exactly 3:1, so average each group of
        // 3 stereo frames down to one mono sample (the averaging also acts
        // as a crude low-pass filter, which keeps speech clean).
        const outSamples = Math.floor(frameCount / 3);
        const mono = Buffer.alloc(outSamples * 2);

        for (let i = 0; i < outSamples; i++) {
          let sum = 0;
          for (let j = 0; j < 3; j++) {
            const off = (i * 3 + j) * 4;
            sum += (data.readInt16LE(off) + data.readInt16LE(off + 2)) / 2;
          }
          let sample = Math.round(sum / 3);
          if (sample > 32767) sample = 32767;
          if (sample < -32768) sample = -32768;
          mono.writeInt16LE(sample, i * 2);
        }

        outBuffer = Buffer.concat([outBuffer, mono]);

        while (outBuffer.length >= CHUNK_BYTES) {
          sttSocket.send(outBuffer.subarray(0, CHUNK_BYTES));
          outBuffer = outBuffer.subarray(CHUNK_BYTES);
        }
      });
    });

    sttSocket.on('message', (raw) => {
      let data;
      try {
        data = JSON.parse(raw.toString());
      } catch {
        return;
      }
      // AssemblyAI sends a few message types (Begin, Turn, Termination) —
      // we only care about "Turn" messages, which carry live transcript text.
      // AssemblyAI sends an Error frame with a readable reason just before
      // it closes a failed session — surface it instead of guessing.
      if (data.type === 'Error') {
        console.error(`❌ AssemblyAI error for ${speakerName}: [${data.error_code}] ${data.error}`);
        return;
      }
      if (data.type === 'Begin') {
        console.log(`   session confirmed by AssemblyAI (id: ${data.id})`);
        return;
      }
      if (data.type !== 'Turn' || !data.transcript) return;

      sendCaption({
        speaker: speakerName,
        speakerId: userId,
        text: data.transcript,
        final: data.end_of_turn === true,
      });
    });

    opusStream.on('end', () => {
      // Flush anything left, then terminate the session properly.
      // Sessions that aren't closed cleanly stay open (and billed) for hours.
      if (sttSocket.readyState === WebSocket.OPEN) {
        if (outBuffer.length > 0) {
          sttSocket.send(outBuffer);
          outBuffer = Buffer.alloc(0);
        }
        sttSocket.send(JSON.stringify({ type: 'Terminate' }));
        setTimeout(() => {
          if (sttSocket.readyState === WebSocket.OPEN) sttSocket.close();
        }, 1000);
      }
      activeStreams.delete(userId);
    });

    sttSocket.on('close', (code, reason) => {
      console.log(`🔌 AssemblyAI connection closed for ${speakerName} (code ${code}${reason ? `: ${reason}` : ''})`);
      activeStreams.delete(userId);
    });
    sttSocket.on('error', (err) => console.error(`❌ AssemblyAI error for ${speakerName}:`, err.message));
  });
}

process.on('unhandledRejection', (err) => {
  console.error('❌ Unhandled error:', err);
});

client.login(DISCORD_TOKEN);