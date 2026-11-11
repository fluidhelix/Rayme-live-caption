# Windows playback captions

This adds an independent, local accessibility mode for conversations and vocal reactions in **any Windows playback audio**. `index.js`, `indexold.js`, `overlay.html`, and `START CAPTIONS.bat` are unchanged. The new mode never initializes Discord or needs its token. Mixed output has no reliable speaker identification; simultaneous voices, music and game sounds can reduce recognition accuracy.

## Phase 2 — application-aware mode

See [PHASE2_REPORT.md](PHASE2_REPORT.md) for current setup, test evidence, changed files, limitations and the mandatory Windows 11 manual checklist. Use **START WINDOWS CAPTIONS.bat** or the retained VALORANT-named launcher. Select and explicitly enable up to two application process trees, or enable whole-device fallback. Nothing is captured by default. Pause stops workers; Clear History removes server replay and updates all connected displays. The application helper now uses **.NET 10** and pinned **NAudio.Wasapi/Core 3.3.0**, still self-contained for end users.

## Shared installation details

Phase 3 adds a native floating caption window. See [PHASE3_REPORT.md](PHASE3_REPORT.md) for the implementation, evidence, developer commands and multi-monitor test checklist.

1. Update to this feature branch/PR. Keep your existing `.env` and `whisper` installation. Node.js 22.12+ and your existing whisper-server/CUDA DLLs and model are required. No Python, .NET runtime, SDK, Visual Studio or virtual audio cable is needed by the end user.
2. Open the repository's **Actions → Windows caption helper** workflow. Select a successful run for this branch/commit and download **windows-loopback-win-x64** (GitHub sign-in may be required). Extract `LoopbackCapture.exe` into `native\bin\LoopbackCapture.exe`. `SHA256.txt` is provided with the artifact. No helper binaries or models are committed. On ARM Windows, x64 emulation is unverified.
3. Keep your existing `.env`. The new mode honors `WHISPER_SERVER_EXE`, `WHISPER_MODEL`, `WHISPER_GPU`, `WHISPER_THREADS`, `FLASH_ATTN`, `WHISPER_PORT`, and `WHISPER_LANGUAGE`. If paths are unset, it searches `whisper` up to three folders deep, preferring a server next to CUDA/Vulkan DLLs and `ggml-large-v3-turbo.bin`. It does not download or duplicate the model. `.env.example` is a reference, not a replacement for private settings.
4. Download **windows-caption-overlay-win-x64** from the same successful Actions run. Extract the artifact into `native\bin`, including `CaptionOverlay.exe`. Compare `Get-FileHash .\native\bin\CaptionOverlay.exe -Algorithm SHA256` with the hash in that artifact's `SHA256.txt`. This is a self-contained x64 executable; no .NET installation is needed. Start it through the launcher, rather than double-clicking the executable: it needs a private session handoff.
5. Close the Discord caption mode. Double-click **START WINDOWS CAPTIONS.bat**. First launch installs the locked Node dependencies if missing. The native floating window opens while the existing Whisper runtime initializes. Open **Controls**, wait for readiness, refresh availability if needed, and select/enable Application A/B OR whole-device fallback. Nothing is captured until enabled. If the native executable is missing or crashes, the backend reports the problem and opens the existing browser fallback. Keep the console open; Ctrl+C stops owned overlay/capture/Whisper processes.
6. Drag the **RAYME** header to any monitor. Resize from the edges or lower-right grip. **Lock** prevents accidental moving/resizing; **Always on top** starts enabled. Controls provide independent text size, background opacity, readable accent palettes, pause/resume and Clear History everywhere. **Hide** leaves a notification-area icon. Double-click the Rayme tray icon to show the window and disable click-through. Its menu can also unlock and restore placement to the primary monitor. Click-through is optional, requires a working tray icon, and is never restored automatically on a new launch.
7. Preferences and placement save automatically in `%LOCALAPPDATA%\Rayme\CaptionOverlay\settings.json`. Captions, source selections and credentials are not saved. To reset corrupt or unwanted preferences, stop the caption session and delete this file. The window clamps its restored bounds into a connected monitor's work area and recovers after display/DPI changes. Physical three-monitor behavior still needs the checklist in the Phase 3 report. This is an external desktop window; exclusive-fullscreen visibility and game/anti-cheat compatibility are unverified.

If the selected endpoint is removed, captions discard the interrupted segment and retry that same endpoint every three seconds. Select another device or **Refresh devices** if necessary. Default output is resolved again on reconnection; changing the system default while the original endpoint stays active requires reselecting default in the UI. No microphone endpoints are enumerated or accepted.

## Configuration and troubleshooting

Optional `.env` values specific to this mode:

| Variable | Default | Meaning |
| --- | --- | --- |
| `WINDOWS_AUDIO_DEVICE` | empty / `default` | Endpoint ID from `native\bin\LoopbackCapture.exe --list`; UI selection overrides it for this session. |
| `WINDOWS_CAPTION_PORT` | `8090` | Loopback-only UI HTTP/WebSocket port. |
| `WINDOWS_SILENCE_RMS` | `80` | PCM energy floor (0–32767). Lower if quiet speech/laughter is missed. Zero disables gating but sends silence to Whisper and may hallucinate. |

Whisper stays loaded. A real `GET /health` JSON `{"status":"ok"}` is required before capture starts (up to two minutes). Old servers without this endpoint need a newer compatible whisper.cpp server **using the same existing model**. The app never assumes readiness based on elapsed time or a console message. The Whisper port must be free; do not run both modes simultaneously. DLL loading uses the executable directory and its PATH. Check your existing CUDA drivers/runtime installation if startup fails. A stopped backend shows an error status; restart after fixing it. The app does not promise a GPU was selected: verify GPU usage locally. Windows mode uses beam size/best-of 5, full model context, four default threads and one inference at a time to favor sentence quality. GPU load and latency need local tuning/testing.

Audio is converted through NAudio PCM/float decoders, channel averaging and the managed WDL resampler to 16 kHz mono signed little-endian PCM. A clocked 100 ms reader supplies silence when WASAPI delivers no callbacks. The native source buffer is capped at two seconds; overflow exits/restarts capture rather than accumulating delay. Node uses 20 ms frames, 240 ms pre-roll, a 900 ms pause boundary, at least 200 ms audible content, and a 15 second hard segment boundary. Partial snapshots arrive at most every 2.5 seconds. This energy gate is intentionally not a speech classifier: laughter/coughs/sighs can pass, as can game audio. Continuous conversations split at the hard limit and may split words at that boundary. No tactical interpretation or player tracking exists.

One shared inference runs at a time, with at most two queued finals and one replaceable partial **per source**, and fair service between sources. Overload drops older queued finals with a visible warning. In-flight partials superseded by a final are suppressed. Failed HTTP/non-JSON responses become status messages, never captions. Device changes discard queued work and invalidate active results; the active inference is allowed to finish so reconnects cannot pile up native GPU work. Shutdown aborts the HTTP request. Whisper annotations such as `[laughter]`, `*coughing*`, and `*sighing*` are preserved. Explicit blank/silence markers are suppressed. Recognition and vocal-cue production depend on the model and source audio; preservation does not guarantee detection.

The UI replays its latest partial and up to 30 finalized captions after reconnect. History is in process memory only. Clear History removes finalized and partial history from server replay and broadcasts the clear to every display; reconnect does not restore cleared entries. No audio/transcript files are written. The HTTP server serves only the caption page, binds to 127.0.0.1, uses a random session URL and checks WebSocket origin/token. No audio is sent to external services. Existing historical `indexold.js` is not used by this mode.

## Development and tests

```
npm ci
npm test
# Windows developer/build machine only (.NET 10 SDK):
dotnet run --project native/LoopbackCapture.Tests -c Release
dotnet publish native/LoopbackCapture -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/windows-loopback
```

`native/LoopbackCapture` depends on NAudio.Wasapi/Core 3.3.0 and the .NET 10 self-contained runtime. The workflow builds on Windows, runs Node tests and native synthetic tone/silence conversion checks, validates helper enumeration (CI may have no output devices), then uploads the helper artifact. The native synthetic tests cover float stereo 48 kHz, PCM stereo 44.1 kHz/48 kHz 24-bit, mono 16 kHz, extensible float stereo, duration, level and silence. They cannot validate an actual endpoint. GitHub Actions must pass before treating the artifact as available.

Cloud Node tests use synthetic PCM and local mock HTTP/WebSocket servers; they cover framing, silence/short reactions/continuous segmentation, memory/queue bounds, readiness and startup failure, HTTP errors, caption ordering, cancellation, device disconnect, child shutdown, reconnect snapshots and unchanged Discord files. Browser tests should exercise the new page with a local fixture. No private recordings or real Discord token are used.

Still required on the user's PC: build artifact execution, actual WASAPI formats/devices, headset unplug/replug/default changes, long silences, shutdown via console X, CUDA readiness/load, large-v3-turbo accuracy, vocal cues, mixed voices/game sounds, readable history/latency and gaming performance. Test browser audio first, then gameplay. The application uses only documented playback capture and ordinary browser windowing. It has no memory access, injection, rendering hooks, input automation, game network interception or game-state access. No Riot approval or guaranteed Vanguard/game compatibility is claimed.

## Phase 1 historical validation record (superseded for Phase 2 by PHASE2_REPORT.md)

Executed in Linux: 11 Node tests passed; Chromium UI integration passed with a real local WebSocket server; .NET 8.0.415 native/test projects compiled with zero warnings/errors; managed synthetic conversion tests passed (five input formats and silence, plus extensible float stereo coverage). Managed conversion running on Linux does not validate WASAPI capture. Windows workflow execution and actual endpoint/GPU checks remain separate.

The self-contained win-x64 executable was also successfully cross-published in Linux (not executed there). This is compile/package evidence only, not Windows endpoint validation. The Windows Actions workflow is the supported source of downloadable build artifacts.
