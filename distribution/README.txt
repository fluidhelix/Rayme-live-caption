RAYME SMART LIVE CAPTIONS 1.0.0 — WINDOWS 11 x64 PRERELEASE

GET STARTED
1. Download the portable ZIP and SHA256SUMS.txt from this repository's GitHub
   Releases page: https://github.com/fluidhelix/Rayme-live-caption/releases
2. Extract the ENTIRE ZIP into a folder where you can read and run files.
   Do not run from inside the ZIP and do not copy Rayme.exe by itself.
3. Double-click Rayme.exe. No developer tools, Node.js, .NET installation,
   account, microphone permission or administrator privileges are required.
4. In Controls > Speech Recognition, explicitly choose an existing whisper.cpp ggml .bin model, or choose
   Download Large-v3-Turbo. The official model is about 1.63 GB; allow at least
   2.2 GB of disk space and 4 GB of free RAM. A valid interrupted download can
   be resumed. The model is verified against its pinned upstream checksum. Keep your own
   models outside the program directory; downloads use your local app data.
5. Once the model is ready, Windows default playback capture starts automatically.
   Use Controls > Audio sources to switch to Application A/B or pause capture.
   Choosing applications keeps fallback off for that session; every new launch
   starts with Windows default playback after model readiness.
6. Drag blank caption surfaces or headers while unlocked; dragging text selects it.
   Resize the floating windows; adjust text size (16–60), opacity and title colors.
   Controls, setup and tray menus share a neutral charcoal theme. The color picker
   supports HEX/RGB, hue and Restore default without changing body text colors.
   Hide keeps captions running in the tray. Closing Controls closes only that
   panel. Quit Rayme stops speech recognition, capture and all owned processes.

CAPTION READING
One enabled application fills the window. With both enabled, A stays above B
in separate continuous transcript panels. Detach is available only with both
applications enabled; choose it explicitly for two independent caption windows.
Reattach from either window or the tray. Disabling a source restores one window.
Whole-device playback uses one panel. Each scrolls independently; captions follow
new speech only while you are near the bottom. Scroll back down to resume follow.
Finalized captions remain during silence and pauses. History is bounded to the
latest 30 finalized segments across sources. Clear history is shared by all clients.
Controls show recent audio signal levels; very quiet speech may need higher
application playback volume. Rayme does not change your Windows device settings.

LOCAL AND OFFLINE
Audio and captions stay on this PC. They are not saved or uploaded. Internet
access is used only when you explicitly download a model. After setup, local
captioning does not require the Internet. Runtime/model settings and downloads
are stored in %LOCALAPPDATA%\Rayme\Runtime; appearance remains in
%LOCALAPPDATA%\Rayme\CaptionOverlay. No .env editing is necessary.
If a selected model is moved, open Controls > Speech Recognition and select it
again. Models are not bundled in the ZIP. CI's tiny.en test model is not a
production default and is never silently installed.

PROCESSING MODES: AUTO / NVIDIA GPU (CUDA) / CPU
Auto is the default. It prefers a compatible NVIDIA GPU and safely falls back to
CPU if GPU discovery or CUDA initialization fails. The bundled CUDA 12.8.1 build
supports RTX 30-series (compute 8.6) and RTX 40-series (8.9), including RTX 4080.
It needs a compatible installed NVIDIA Windows driver (572.61 or newer), not a
CUDA toolkit installation. Drivers are not redistributed or installed by Rayme.
Other GPUs/unsupported drivers use CPU in Auto; explicit CUDA reports an error
rather than claiming GPU acceleration. CPU always works without NVIDIA hardware.

Choose the mode in Controls > Speech Recognition; no .env editing is required. A mode
change stops capture and the old model process before reloading. Your selected sources and pause state return after a successful reload. The UI reports the ACTUAL initialized backend separately
from the requested mode. "CUDA initialized" alone is not proof of acceleration.
After real inference, Rayme checks whether NVIDIA independently reports its
Whisper process. If that evidence is unavailable, GPU activity stays explicitly
unverified. See the Release notes for whether real hardware acceptance was run.
The first release candidate may still need physical RTX 4080 validation.

RECOVERY
Use the Rayme notification-area icon (possibly under the overflow arrow) to
show a hidden overlay, disable click-through, unlock or restore it to the primary
monitor. Click-through starts off on every launch. If a component crashes, the
launcher stops the owned session and offers Retry or Cancel. Retry starts one
fresh session; it does not open Chrome or another browser. Cancel quits.
Rayme never changes playback settings or disables exclusive mode. An unavailable
or busy output must be changed in Controls; consult Windows device settings if
another application has reserved it. The default is Windows' multimedia output.

REQUIREMENTS AND LIMITS
The Release notes state the exact ZIP and extracted sizes for each build. Bundled
CUDA libraries make this a large package; the additional model is not included.
Windows 11 x64, a working playback endpoint, and enough memory for the selected
model. ARM and older Windows versions are unverified/unsupported. Process audio
isolation uses documented Windows APIs; source names identify applications, not
individual speakers or browser tabs. This is a normal desktop window, not an
injected game overlay. Exclusive-fullscreen visibility, game/anti-cheat approval
and perfect recognition are not promised. Keep it on another monitor or use a
normal windowed/borderless arrangement.

INTEGRITY / SECURITY
SHA256SUMS.txt on the Release page verifies the downloaded ZIP. INTEGRITY.json
inside verifies every shipped file; Rayme checks it before startup. To verify
the ZIP manually, Windows PowerShell's Get-FileHash -Algorithm SHA256 can be
compared with SHA256SUMS.txt (optional for ordinary setup). These checks detect
damage; the executables are unsigned. Windows/organization policy may show an
unknown-publisher warning. No guaranteed SmartScreen/Defender approval is claimed;
do not disable security features. Download only from the official repository.

ATTRIBUTION AND VERSION
VERSION.json identifies the exact source commit and bundled versions. See
LICENSE.txt, Licenses, and RELEASE-NOTES.md for licensing and limitations.
Included: Node.js (with its third-party licenses), ws (MIT), .NET and Windows
Desktop runtime (MIT and third-party notices), NAudio (MIT), Cockos WDL resampler,
whisper.cpp/ggml (MIT), cpp-httplib (MIT) and nlohmann JSON (MIT). NVIDIA CUDA
redistributable runtimes retain NVIDIA's included license terms. Optional model
downloads use OpenAI Whisper weights, released under MIT, converted by whisper.cpp.

This is a prerelease for final Windows acceptance testing. Development artifacts
and UI/CI diagnostics are not the official end-user distribution. Stable release
promotion requires explicit authorization after real-machine acceptance.
