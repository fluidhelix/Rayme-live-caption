# Phase 2 developer report

## Baseline and scope

Delivery branch: `feature/windows-loopback-captions` (draft PR #1). Native implementation commit: `0d6e57b`; two-source pipeline commit: `2d52a9a`. Follow-up delivery commits include final timing correction and this report; use the branch HEAD for testing.

Verified the existing clean checkout at `feature/windows-loopback-captions`, HEAD `51bfa64`, before editing. Continued on that same branch to preserve draft PR #1's relationship; no merge into main. The original Discord `index.js`, `indexold.js`, `overlay.html`, `START CAPTIONS.bat`, and `package-lock.json` are unchanged. Phase 1 whole-device capture and tests remain; the Windows UI now requires explicit enablement before any capture.

## Implemented

- **Discovery/capture:** .NET 10; exact NAudio.Wasapi and NAudio.Core **3.3.0** pins. APIs checked against the actual NuGet XML and upstream source at `4f8889e2bd34c3978a25652c6c172fa1502ccba3`, specifically `Docs/WasapiRecorder.md` and `src/NAudio.Wasapi/WasapiRecorderBuilder.cs`. Uses `new WasapiRecorderBuilder().WithProcessLoopback(pid, ProcessLoopbackMode.IncludeTargetProcessTree).BuildAsync()` with activation timeout. Never substitutes endpoint capture when activation fails. Windows 11 build 22000+ is the intentionally conservative process-mode gate; unsupported systems expose whole-device fallback.
- **Identity:** enumerate render-endpoint Core Audio sessions; obtain ordinary process name, PID and start-time ticks. Collapse same-executable ancestors to capture browser/Discord process trees. Group duplicate sessions, omit inaccessible metadata, reject overlapping selected trees when their ancestry is discoverable. Identity is PID + start time, not PID or text-derived labels. A process restart requires refresh and explicit reselection; it is never automatically rebound to a reused PID.
- **Two sources:** independent worker, segmenter, generation, local IDs, finalized watermark, queues and partial state per source. Exactly one shared Whisper server/model and one active HTTP inference globally. Round-robin service of finalized work; at most two queued finals and one replaceable partial per source. Finals take precedence over speculative partials. Overload drops older queued finals with visible delayed status. Active work is invalidated rather than aborted during source changes so native GPU work cannot accumulate behind cancelled HTTP requests. Shutdown aborts outstanding requests.
- **Attribution:** source descriptor frozen at scheduling time. Results require the same scheduler source-state object/generation; removal, pause, restart and replacement invalidate old results. Capture time is estimated from the first-PCM-arrival anchor and precise PCM sample offsets, independent of pipe chunking. History sorts by segment start time; this is **not** sample-accurate cross-application synchronization: raw PCM protocol does not transport WASAPI QPC packet timestamps or compensate native/device buffering. No audio streams are mixed. Whisper starts with `-nc`, and inference includes `no_context=true` to avoid carrying prior-source context.
- **UI/control:** two application selectors and enable checkboxes, named independent partials, source statuses, 30 finalized entries, inference duration/queue-age metrics, readable text sizing, pause/resume, refresh and independent removal. Whole-device capture and application workers are mutually exclusive. Pause closes workers, not merely the text display; resume creates fresh generations. Controls are serialized in the orchestrator. Server-side Clear History clears finalized/partial replay state and broadcasts to every display; reconnect cannot resurrect cleared entries. New inference results can still arrive after clearing if capture continues.
- **Privacy/safety:** PCM stays in memory/stdout pipes and local inference HTTP. No transcripts/audio persistence, no Discord token, no admin/driver/cable requirement, no game integrations. Existing vocal annotations remain. Process names are actual process names (for example `chrome`), never inferred websites/tabs or automatically asserted “VALORANT Voice Chat.”

## Changed files

| File | Change |
| --- | --- |
| `global.json` | Require .NET 10 SDK with feature-version roll-forward. |
| `native/LoopbackCapture/LoopbackCapture.csproj` | net10.0-windows and exact compatible NAudio package pins. |
| `native/LoopbackCapture/Program.cs` | Session discovery command, asynchronous process/endpoint recorder, span conversion, identity checks and explicit failure exit codes. Existing `--list`/`--device` and raw 16 kHz mono PCM stdout remain. |
| `native/LoopbackCapture/Discovery.cs` | Session candidates, process/start-time identity, parent metadata, grouping and ancestry. |
| `native/LoopbackCapture/CapturePolicy.cs` | Identity checks before/after activation and disposal on restart races, with injectable activation for deterministic tests. |
| `native/LoopbackCapture.Tests/LoopbackCapture.Tests.csproj`, `Program.cs` | .NET 10 conversion, discovery, identity, activation failure/race cleanup and buffer-bound tests. |
| `windows-captions/audio.js` | PCM-derived segment capture offsets; preserve Phase 1 segmenter/scheduler behavior. |
| `windows-captions/capture.js` | Process command arguments, validated discovery metadata, terminal process errors and source status. Device reconnect fallback retained. |
| `windows-captions/sources.js` | New fair shared scheduler and independent source lifecycle manager. |
| `windows-captions/main.js` | One Whisper instance; source discovery/control orchestration, serialized controls and coordinated cleanup. |
| `windows-captions/whisper.js` | Disable transcription context carryover across requests/sources. |
| `windows-captions/server.js` | Source snapshots/replay, attribution, configuration, pause and actual server history clear. Local token/origin protection retained. |
| `windows-captions/overlay.html` | Two source controls/labels/partials, lifecycle statuses/metrics and synchronized clear. |
| `START WINDOWS CAPTIONS.bat` | Generic launcher equivalent to the retained VALORANT-named Windows launcher. |
| `test/phase2.test.js` | Synthetic multi-source fairness/identity/overload/control/reconnect/history tests. |
| `test/overlay-browser.py` | Two-source UI integration, multiple display clearing/reload, pause/resume and enable controls. |
| `.github/workflows/windows-captions.yml` | .NET 10, native/Node/browser tests, endpoint/application enumeration smoke checks and self-contained artifact. |
| `WINDOWS-CAPTIONS.md` | Updated Phase 2 usage, requirements and link to this detailed report. |
| `PHASE2_REPORT.md` | This implementation, validation, limitations and manual checklist. |

## Executed validation

- Node suite: **20 passed**, including retained Phase 1 regressions; no Discord credentials or private samples.
- Chromium/Playwright: **passed**, using a real local WebSocket fixture with two independent source labels/partials, 30-entry history, font adjustment, reconnect, pause/resume, two-display clear and reload, and independent source disable.
- .NET 10.0.403 native/test projects: compiled successfully. Native executable test harness: **10 PASS groups** covering five input formats, silence, session discovery/grouping/missing metadata, PID reuse, activation failure/restart cleanup, and bounded native buffering. These pure conversion/metadata/policy checks ran in Linux; they do not exercise COM audio activation.
- Self-contained single-file Windows x64 helper cross-published successfully. This is packaging evidence; the executable was not run in Linux.
- `git diff --check` and protected Discord/lockfile comparison pass.

Phase 2 Windows CI status is **unverified**. GitHub API queries return `Forbidden` in this environment. User reports Phase 1 CI passed; that does not establish Phase 2 CI readiness. The updated workflow is [Windows caption helper](https://github.com/fluidhelix/Rayme-live-caption/actions/workflows/windows-captions.yml), artifact **windows-loopback-win-x64** after a successful run for the current commit. No confirmed run-specific artifact link is available. Push this branch to update draft PR #1; this report is the prepared PR summary if the API remains blocked.

## Exact setup on Windows 11 x64

1. Update the existing feature branch files. Keep existing `.env`, Whisper server/model and CUDA DLLs; do not replace or copy the model. Install no SDK/.NET runtime on the end user's PC.
2. Download **windows-loopback-win-x64** from a successful **Phase 2 commit** Actions run and extract `LoopbackCapture.exe` into `native\bin`. Replace the Phase 1 helper. The workflow supplies SHA256. Developers can build with `.NET 10 SDK`: `dotnet publish native/LoopbackCapture -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -o artifacts/windows-loopback`.
3. Close the Discord caption mode, then double-click **START WINDOWS CAPTIONS.bat** (the existing **START VALORANT CAPTIONS.bat** also starts this Windows mode). Existing Node 22.12+, `.env` Whisper paths and the one existing local model remain required.
4. Wait for real Whisper health readiness. Nothing is captured by default. Play audio in candidate apps and click **Refresh applications/devices**. Select a process in Application A and check its enable box. Enable Application B for a second independent source. Selecting a device fallback disables both application sources; enabling an application disables device capture.
5. Pause closes all capture workers. Resume revalidates the selected identities; an application that restarted must be explicitly selected again after refresh. Disable a source to remove only its worker. Ctrl+C stops all workers and Whisper. Clear History affects every display and server replay, not the audio workers.

## Mandatory real-PC checklist (not executed in cloud)

- **A — Baseline:** choose the normal headset in whole-device fallback; play a YouTube conversation. Verify speech, brief/long silence and vocal reactions, switch devices, unplug/replug and Ctrl+C/console-X shutdown. Confirm no orphan helper/server remains. Default device changes require reselecting default if the previous endpoint remains active.
- **B — Single application:** play clearly distinguishable speech in Chrome and Discord. Enable Chrome only: Discord speech must be excluded. Repeat with Discord only. Verify displayed names/PIDs correspond to discovery. Browser tabs within a selected process tree are combined, not individually identified.
- **C — Simultaneous:** enable Chrome and Discord. Play speech concurrently for several minutes. Check correct immutable labels, separate partials, finalized order and 30-entry history, no cross-source overwrite, bounded queue age and explicit delay/drop reports. Open a second display of the current page; clear, reload both and confirm old entries stay gone.
- **D — Restart:** terminate one selected app while inference is active. Verify no stale result appears under a replacement's label, the other source continues, and the unavailable app does not silently capture a reused PID. Refresh and explicitly choose the restarted identity. Also test permissions/inaccessible process and OS/activation rejection; these must never fall back to mixed output automatically.
- **E — Performance:** during sustained dual speech, monitor UI inference duration/queue age plus Task Manager CPU, GPU and memory. Record observations manually; the application writes no conversation logs. Expect speculative partials to reduce first and older finalized jobs to be skipped when overloaded. Test pause/resume, repeated enable/disable, source removal and switching between process/device modes. Tune the shared existing model/runtime locally if needed.

## Limits / unverified behavior

Real WASAPI process activation/session enumeration, Windows 11 playback exclusion, protected audio/app behavior, actual headset conversion, GPU selection/load, caption accuracy/readability and game compatibility are unverified. Session discovery is a snapshot, not a persistent background watcher: play audio and refresh; inactive/new/packaged/protected apps may not be discoverable, and inaccessible process metadata is intentionally omitted. Friendly names are executable process names, with PIDs shown. Same-executable parent grouping is conservative and cannot guarantee a whole packaged/browser application maps to one discoverable tree. Ancestor overlap rejection is best-effort when all metadata is accessible. Capture covers the selected process **and descendants**, potentially across playback devices, not an individual chat stream/tab. A selected root exiting stops its capture, even if descendants survive. No automatic PID/name-based reattachment occurs. Hardware/driver routing, protected content, services emitting audio for another app and games using separate voice processes need manual inspection; no verified voice-chat identity is asserted.

The native activation timeout reports failure; source process failures are terminal until explicit reselection. Endpoint failures retry the same endpoint. Raw PCM stdout protocol means timestamps are estimated and limited by native/device buffers and first PCM arrival. Exact WASAPI-QPC alignment is not implemented. Hard 15-second segments can split words; simultaneous voices within one app remain mixed. Clear during capture does not cancel an already-running inference: new captions may arrive afterward. Closing only the browser leaves the console session alive. Windows console-X cleanup still needs local verification; Ctrl+C is the supported graceful path.

No Riot approval, Vanguard approval or actual VALORANT compatibility is claimed. No game memory, rendering hooks, injection, input automation, game-state inspection or network interception is used.
