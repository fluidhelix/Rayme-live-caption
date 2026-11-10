# Add independent Windows playback captions without changing Discord mode

Windows users can capture a selected playback endpoint through WASAPI loopback and read local Whisper captions in a separate browser window. The new launcher does not initialize Discord or need a bot token. Existing Discord source, historical source, overlay and launcher are unchanged.

The capture helper uses NAudio.Wasapi/Core 2.2.1, converts supported PCM/float formats to 16 kHz mono through a managed WDL resampler, and streams raw PCM to Node without recording files. Segmentation, bounded inference queues, verified backend health, reconnect/reset handling and in-memory caption history are independent of the Discord implementation. Useful vocal annotations are preserved.

## Files

| File | Change |
| --- | --- |
| `.gitignore` | Protect private settings, local dependencies, native runtimes/models, recordings, transcript logs, preferences and build outputs. |
| `.env.example` | Safe reference for existing Whisper paths and Windows-only options; no token required. |
| `package.json` | Add `start:windows` and `test`; preserve `start` and all dependencies. Lockfile unchanged. |
| `START VALORANT CAPTIONS.bat` | Check Node/helper, install locked dependencies if needed, launch independent entry point and show failures. |
| `windows-captions/main.js` | Orchestrate local UI, Whisper, capture, device selection, startup and shutdown. |
| `windows-captions/audio.js` | WAV encoding, framed energy/pause segmentation and bounded serialized inference scheduling. |
| `windows-captions/whisper.js` | Discover existing runtime/model, run child with DLL search path, verify health and validate local HTTP inference responses. |
| `windows-captions/capture.js` | Enumerate playback devices, manage helper lifetime, reset interrupted audio and retry disconnects. |
| `windows-captions/server.js` | Loopback-only HTTP/WebSocket UI, session-token/origin checks and bounded replay state. |
| `windows-captions/overlay.html` | High-contrast adjustable captions, device selector/status, partial/final separation and 30-entry history. |
| `native/LoopbackCapture/LoopbackCapture.csproj` | .NET 8 Windows console helper with narrowly scoped NAudio.Wasapi dependency. |
| `native/LoopbackCapture/Program.cs` | Playback-only WASAPI capture, common-format conversion, clocked silence, bounded native buffer and machine-readable status. |
| `native/LoopbackCapture.Tests/LoopbackCapture.Tests.csproj` | Synthetic conversion test executable referencing production conversion. |
| `native/LoopbackCapture.Tests/Program.cs` | Five source-format cases plus silence, with duration/level/alignment assertions. |
| `.github/workflows/windows-captions.yml` | Windows Node/native tests, self-contained win-x64 publish, enumeration smoke check, SHA256 and downloadable helper artifact. |
| `test/windows.test.js` | 11 automated tests of audio handling, readiness/errors, scheduling, cancellation, device loss, shutdown, UI replay/security and Discord source preservation. |
| `test/overlay-browser.py` | Optional developer Chromium integration with synthetic captions and a real local WebSocket server. |
| `WINDOWS-CAPTIONS.md` | End-user setup, launch/device selection, configuration, architecture limits, privacy, build/test instructions and Windows validation checklist. |
| `PR_DESCRIPTION.md` | Review/delivery summary and per-file inventory. |

## Launch

Keep the existing Node/Whisper/CUDA installation and `.env`. Download **windows-loopback-win-x64** from a successful **Windows caption helper** Actions run for this commit; extract `LoopbackCapture.exe` to `native\bin`. Double-click **START VALORANT CAPTIONS.bat** and select the normal headset playback output. Adjust text size and move/resize the browser normally. Ctrl+C in the console stops the session. Neither launch mode needs to run alongside the other.

End users do not install a .NET SDK/runtime, Python, Visual Studio, audio cable software or another recognition framework. The helper is self-contained. No models or binaries are committed/downloaded by the production app.

## Executed validation

- `npm test`: **11 passed, 0 failed**.
- `python3 test/overlay-browser.py`: **passed** in Chromium: real WebSocket snapshot/reconnect, 30-entry history, vocal cue, font adjustment and clear control.
- .NET 8.0.415 build of helper/test projects: **passed, zero warnings/errors**.
- `dotnet run --project native/LoopbackCapture.Tests -c Release`: **six passed checks**: 48 kHz float stereo, 44.1 kHz PCM stereo, 48 kHz 24-bit PCM stereo, 16 kHz PCM mono, extensible 48 kHz float stereo, and silence.
- Self-contained single-file `win-x64` cross-publish: **passed** in Linux. The Windows executable was not executed in Linux.
- `git diff --check`: **passed**. Existing four Discord source/launcher/overlay files and `package-lock.json` remain unchanged. Credential-pattern scan found no matches in tracked original files; private settings and generated outputs are excluded.

## Outstanding validation and delivery restriction

The cloud machine has no Windows endpoint or NVIDIA Windows runtime. Actual playback capture, device unplug/replug, quiet reactions, mixed voices, long pauses, CUDA behavior, caption accuracy/readability, console-X cleanup and gaming performance require the user's Windows PC. Start with ordinary browser audio. There is no claim of Riot approval or Vanguard/game compatibility.

The pushed branch can trigger the supplied Windows workflow, but this session cannot query the Actions run or create a PR because `api.github.com` is denied by the cloud network policy (HTTP CONNECT 403). Native Git push succeeded using the existing platform authentication. No new GitHub token was requested. A successful Windows workflow run/artifact is not claimed; review its result on GitHub before deployment. The local cross-published executable is compile/package evidence only.
