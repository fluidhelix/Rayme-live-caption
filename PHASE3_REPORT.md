# Phase 3 — native floating caption overlay

Phase 4 supersedes the historical distribution and exit/browser-fallback behavior below. See [PHASE4_REPORT.md](PHASE4_REPORT.md) for Rayme.exe, official prereleases, explicit Quit Rayme and Auto/CUDA/CPU processing.

The modern UI refinement supersedes the initial visual design described below. See [UI_REFINEMENT_REPORT.md](UI_REFINEMENT_REPORT.md) for the shared component system, redesigned Controls/tray, stable partial/final typography and current validation.

Baseline: `75f359784fe53eebb09995f355fcf08b20c48cf4`, fetched from `feature/windows-loopback-captions` before implementation. Work remains on that branch and draft PR #1; no merge into `main` is authorized or performed. The implementation commit is identified in the final handoff and the Actions run's commit link.

## What is implemented

`native/CaptionOverlay` is a WPF .NET 10 Windows x64 client, published as a self-contained executable. It uses software rendering, one native UI process and no additional browser engine. The self-contained distribution bundles the Windows desktop runtime (approximately 166 MiB in the Linux cross-publish); this is download size, not measured working-set usage.

The frameless floating window has rounded translucent corners, a small drag header, edge resizing, a resize grip, 300×180 DIP minimum dimensions, position/size lock and optional always-on-top (default on). Captions wrap naturally. Text size (16–60 DIP) is independent of window size. Background alpha is adjustable from 70–100%; text stays opaque. The floor keeps the small source labels above 4.5:1 contrast even over a white desktop (tested with the actual alpha-byte rounding). Source A/B/playback accents have six named palettes. Labels retain A/B/output identification and a full-name tooltip when narrow; partials now use the same normal typography as finals and a small LIVE badge, retaining the same visual row on completion. Transcript strings are assigned to `TextBlock.Text`, never interpreted as markup. The conversation scroll follows new content only when the reader is already near the bottom.

A separate compact, scrollable native Controls window provides application/device discovery refresh, selection and enablement for A/B or whole-device fallback, pause/resume, shared Clear History, appearance, locking, topmost, placement reset and optional click-through. Application choices include PID to distinguish similarly named processes. Every capture command goes to the existing server; its validation and `SourceManager` remain the sole configuration authority. The browser interface remains functional.

The notification-area menu shows the overlay, disables click-through, opens Controls, or unlocks/restores it to the primary display. Double-click also shows the window and disables click-through. Click-through starts off on every launch and cannot be enabled if notification-area initialization fails. No global hotkeys/hooks, input injection or game integration are used. **Hide** keeps the session running in the tray. Exiting the overlay triggers the browser fallback; stopping the backend ends the owned overlay.

## Session, state and process ownership

- `windows-captions/native-overlay.js` starts exactly one intended native child for the backend session. The random authenticated session URL is passed through inherited stdin, not argv, environment, preferences or logs. Stdout carries only a bounded readiness acknowledgement, never captions or credentials. The pipe stays open for the backend's lifetime. EOF closes WPF even if Node exits unexpectedly. Normal shutdown gives WPF 1.5 seconds to close/save, then enforces owned-process cleanup. A crash never deliberately terminates transcription; the existing browser is opened once as fallback. The browser fallback retains its existing URL handoff behavior.
- `CaptionOverlay.Core/SessionClient.cs` accepts only `http://127.0.0.1:<port>/?token=<48 hex characters>` sessions. It uses the existing WebSocket endpoint with the exact HTTP Origin header; authentication/origin validation in `server.js` is unchanged. One receive loop and one socket exist at a time, with an 8-second connect deadline, 2 MiB message ceiling, bounded exponential reconnect delay (1–10 seconds), and no offline control queue. UI dispatch is awaited before reading the next event. Failed controls visibly ask the user to retry.
- `CaptionState.cs` applies authoritative snapshots on every connection, replaces prior state rather than appending it, keeps independent partials and final watermarks by source/generation, suppresses duplicate finals/late partials, preserves source attribution and annotations, and orders finals stably by capture timestamp (including fractional timestamps). Display history is capped at 30. Source removal clears its displayed rows; replaced identities are filtered out of reconnect history. Capture resets for the same identity can retain historical finals while invalidating partial/in-flight generations. Backend replay remains unchanged. Pause/resume and Clear History use existing broadcasts across clients.
- Preference records contain only appearance and placement. Unknown schemas, malformed/oversized files, invalid colors and invalid/nonfinite values recover to safe defaults. Writes use a temporary file and replace. Write failures are visible. Click-through, transcripts, session URLs/tokens, application choices and audio are never persisted.

## Multi-monitor placement

The executable declares PerMonitorV2 awareness. WPF manages its DIP layout and DPI transitions; persisted window rectangles use physical desktop pixels. The native adapter uses documented `EnumDisplayMonitors`, `GetMonitorInfo`, `EnumDisplayDevices`, `GetDpiForMonitor`, `GetWindowRect`, `MonitorFromWindow`, `SetWindowPos` and extended window styles. Monitor interface identities are preferred to ordinal DISPLAY1/2 names. It does not change display or device settings.

The pure placement routine supports negative X/Y coordinates, portrait work areas, per-monitor scale, preferred monitor restoration, minimum dimensions, oversized saved windows and missing monitors. Bounds are clamped completely into an available work area. Display/DPI/work-area notifications schedule recovery without activating the overlay. The tray and Controls provide an explicit placement reset. The overlay does not steal focus on ordinary caption updates or placement recovery; explicit user recovery activates it.

## Validation evidence

These are distinct forms of evidence; cross-compilation does not establish Windows runtime behavior.

| Check | Evidence from this implementation session |
|---|---|
| Linux Node suite | All 33 tests pass, including five retained endpoint-selection regressions, existing security/source tests, three native-launcher regressions and unchanged Discord-file integrity assertions. |
| Linux native overlay core | 39 assertions pass: interleaved sources, partial/final replacement, annotations/literal text, duplicate/out-of-order suppression, removal/generation invalidation, clear/pause/resume, snapshots, history bound, settings validation/serialization/corrupt recovery, text contrast floor, negative coordinates, mixed DPI bounds, removed monitors, equal/fractional timestamps and restricted session URLs. |
| Linux actual native-to-backend protocol | Nine .NET assertions plus Node fixture assertions pass using the real `ClientWebSocket` and production Node server. Valid token/Origin connection, invalid token/origin rejection, commands, reconnect snapshot without duplicate entries and Clear History observed by another connected client. Only caption inputs are synthetic. |
| Linux retained browser UI | Playwright/Chromium test passes: two labels/partials, bounded history, reconnect, font controls, pause/resume, clear across clients/reload and source disable. |
| Linux existing native tests | All existing conversion/discovery/policy/buffer checks pass: five input formats, silence, PID identity and activation cleanup. These are managed tests, not WASAPI device execution. |
| Linux WPF compilation/package | Release Windows x64 cross-publish succeeds, zero build warnings/errors after the targeted WPF/NotifyIcon DPI analyzer suppression. `CaptionOverlay.exe` is generated. It cannot run on Linux. |
| Windows CI in this session | **Not verified.** GitHub Actions API requests return `403 Forbidden`. The new required `caption-overlay` job compiles/tests/publishes, runs packaged private-pipe/EOF lifecycle and synthetic WPF smoke tests, computes SHA256 and uploads `windows-caption-overlay-win-x64`. No continue-on-error or synthetic audio/inference substitute is introduced. |
| Existing real Windows audio/Whisper | User-reported baseline success is preserved, including the default multimedia endpoint fix. This session did not rerun real Windows audio or model inference. Existing checksums, pinned assets, isolation checks and real Whisper accuracy requirements are untouched. |

The packaged synthetic GUI test verifies that WPF constructs the real window and Controls, builds two caption rows and meets minimum dimensions. It is **not a visual PASS**, a monitor interaction test or an accessibility-reader certification. The packaged lifecycle test waits for an actual loaded-window acknowledgement, rather than assuming startup after a sleep, then verifies clean shutdown on parent-pipe EOF.

Workflow: https://github.com/fluidhelix/Rayme-live-caption/actions/workflows/windows-captions.yml

Draft PR: https://github.com/fluidhelix/Rayme-live-caption/pull/1

Updating the draft PR description was attempted but also returned API `403 Forbidden`. Pushing the existing head branch updates its code diff; metadata edits and CI status cannot be verified from this environment. The PR is not marked ready or merged.

## Windows installation and launch

1. Update the existing checkout to the implementation commit on `feature/windows-loopback-captions`. Keep the existing `.env`, Whisper executable/DLLs and model. Node.js 22.12+ remains required; no extra .NET installation or administrator privileges are needed.
2. In **Actions → Windows caption helper**, select a successful run for that exact commit. Download `windows-loopback-win-x64` and `windows-caption-overlay-win-x64`. Extract their contents into `native\bin` so both `native\bin\LoopbackCapture.exe` and `native\bin\CaptionOverlay.exe` exist. Do not use an artifact from a different commit to claim this change passed CI.
3. Verify each executable with `Get-FileHash <path-to-executable> -Algorithm SHA256` against the hash included in its own artifact. Each artifact has its own `SHA256.txt`; compare before extracting the second into the shared folder or keep their hash files separately. Use the Windows CI artifact hash, not a Linux cross-publish hash.
4. Close the old caption mode. Double-click `START WINDOWS CAPTIONS.bat`. The launcher installs locked Node dependencies if absent, starts the backend, opens the native overlay through the private handoff, then initializes the existing Whisper runtime. Do not start `CaptionOverlay.exe` manually; it requires the backend's session pipe.
5. Open **Controls**, wait for Whisper readiness, refresh applications, select and enable A/B OR whole-device fallback, and play conversation audio. Move the header onto the upper monitor, resize, set text size/opacity/colors, then Lock if desired. Capture does not start merely because the overlay opens.
6. Use the tray icon to recover from Hide/click-through. Use its **Unlock and restore to primary monitor** command if placement is awkward. For a complete preference reset, stop the session and delete `%LOCALAPPDATA%\Rayme\CaptionOverlay\settings.json`.
7. Ctrl+C in the launcher console stops the owned overlay, capture workers and Whisper. Closing Controls leaves captions running; Hide leaves the overlay running in the tray; exiting only the overlay opens the browser fallback while transcription continues.

## Developer build and test commands

```powershell
npm ci
npm test
dotnet run --project native/CaptionOverlay.Tests -c Release
node test/native-overlay-session.js
dotnet run --project native/LoopbackCapture.Tests -c Release
python test/overlay-browser.py # requires the existing Playwright/Chromium test dependencies
dotnet publish native/CaptionOverlay -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/windows-overlay
node test/native-overlay-lifecycle.js artifacts/windows-overlay/CaptionOverlay.exe # Windows only
.\artifacts\windows-overlay\CaptionOverlay.exe --smoke-test # Windows only; synthetic caption UI
Get-FileHash .\artifacts\windows-overlay\CaptionOverlay.exe -Algorithm SHA256
```

The platform-independent overlay core/tests can run on Linux with the .NET 10 SDK. WPF cross-building on Linux is enabled in the project. The native Windows executable and its GUI/lifecycle tests require Windows.

## Remaining limitations and required physical checks

No known compilation or executed-test failure remains. The actual WPF rendering/interaction and packaged pipe lifecycle are not yet executed in this Linux session; CI results must be checked externally. The executable is unsigned. The tray now uses the embedded Rayme icon and a themed WPF flyout, with a styled native menu fallback; users may need to expand Windows' notification-area overflow to find it. Accent selection is a curated six-color palette, not an arbitrary color editor. Click-through is intentionally not persisted.

On the intended Windows 11 three-monitor machine:

1. Confirm native startup, no mandatory browser window, recognizable source choices, two simultaneous labeled sources, partial/final wrapping and the vocal annotations using the real existing Whisper runtime.
2. Move between the front, upper and left portrait monitors at differing scaling factors. Resize every edge and grip; verify clarity, minimum bounds, keyboard/control readability, independent font sizing and no focus stealing during captions. Check saved placement after a normal restart.
3. Lock and verify accidental header/edge gestures do not move/resize. Toggle topmost with ordinary desktop windows. Verify Controls and tray recovery remain accessible, including notification-area overflow and click-through. Hide/show and unlock/reset placement.
4. Unplug the upper or portrait monitor while visible, hidden, locked and click-through; verify recovery onto a visible monitor. Change scale/work-area arrangements and reconnect displays. Check another launch with the saved monitor missing.
5. Keep the browser fallback connected as a second client. Verify pause actually stops capture workers, resume restores them, source replacement never misattributes captions, and Clear History stays cleared in both clients after reconnect. Test network disconnect/reconnect and backend stop/restart without duplicate caption rows or orphaned overlays.
6. Exit or terminate just the overlay: transcription should continue with browser fallback. Stop Node via Ctrl+C and console X: its owned overlay should exit. Remove/rename the native executable to exercise the usable missing-overlay fallback. Restore it afterward.
7. Repeat real conversation accuracy/latency, vocal cues, CPU/memory responsiveness and long-session behavior on the user's production Whisper model/audio setup. These have not been measured end-to-end with the new overlay in this session.

This is a normal desktop window. No Riot/Vanguard compatibility, game approval or exclusive-fullscreen visibility is claimed. Prefer the intended upper monitor or a normal windowed/borderless arrangement pending physical verification. Whisper, source scheduling, production WASAPI capture, endpoint selection, original Discord files and strict integration tests have not been redesigned or weakened.

## Changed files

- Added `native/CaptionOverlay.Core/{CaptionOverlay.Core.csproj,CaptionState.cs,Preferences.cs,SessionClient.cs}`.
- Added `native/CaptionOverlay/{CaptionOverlay.csproj,app.manifest,Program.cs,DesktopPlacement.cs,OverlayWindow.cs}`.
- Added `native/CaptionOverlay.Tests/{CaptionOverlay.Tests.csproj,Program.cs}`.
- Added `windows-captions/native-overlay.js` and integrated it in `windows-captions/main.js`.
- Added `test/native-overlay.test.js`, `test/native-overlay-session.js`, `test/native-overlay-lifecycle.js`.
- Updated `.github/workflows/windows-captions.yml`, `WINDOWS-CAPTIONS.md`; added this report.

`index.js`, `indexold.js`, root `overlay.html`, `START CAPTIONS.bat`, the original Windows launchers, production capture/scheduling/Whisper modules, browser fallback UI/server protocol, audio endpoint test and pinned audio assets are unchanged from the verified baseline.
