# Phase 5B — usability, accessibility and continuous captions

Work began by fetching `origin/main` and comparing the existing checkout. The authoritative baseline was `618f860` (including the merged Phase 5A work and subsequent health corrections). Implementation continues on `main`; no obsolete Phase 5A branch was recreated. No stable release is requested.

## Interactions identified

- Header buttons did not explicitly opt into WPF client-area hit testing. Locking changes `ResizeMode`, causing WindowChrome to recalculate its non-client resize region. Header controls now explicitly remain client hit targets, including after lock transitions; the resize region is zero while locked. Windows smoke tests issue real `WM_NCHITTEST` messages at Settings and exercise repeated lock/close/reopen sequences. Physical reproduction of the reported intermittent failure remains a Windows acceptance item.
- Startup intentionally left source selection empty. Processing/model changes destroyed the manager, removed source state/history and required manual re-enabling. A small session-intent coordinator now remembers descriptors and pause state while the existing SourceManager remains the sole capture authority.
- The old renderer created one visual per segment and used a shared scroll position. Continuous transcript presentation must sit above Phase 5A's segment/revision ledger, rather than replacing its lifecycle rules.
- Model setup and Controls both exposed processing selectors. Guided setup is now part of Controls, with one requested-mode selector, an actual-device report and explicit model-choice state.

## Implemented behavior

**Controls and lock.** Closed padlock means locked; open padlock means unlocked. Tooltip, accessible action name and tray action derive from the same state. Settings is explicitly hit-testable in chrome. Drag handling stays in the drag area; tray recovery, click-through recovery, software rendering and placement persistence remain.

**Startup and model reload.** Each new session selects Windows default whole-device playback once, after the configured model has actually initialized. Existing model and processing preferences load offline without repeated setup. Explicit application selection removes fallback through the existing exclusivity validation; disabling sources does not secretly re-enable fallback. A successful mode/model reload restores the current descriptors and pause state, with old workers stopped first. Failed initialization starts no replacement capture. Shutdown during replacement cannot create a late worker. Normal Quit, owned processes and private handoff are unchanged.

**Fixed transcripts.** Application A permanently occupies the upper half and B the lower half whenever in application mode, including when only one is configured or speech pauses. Whole-device mode has one full-height panel. Labels and accents are persistent. Final sentences append in a single text area; the current partial is a separate normal-typography run in that area and finalization replaces it without duplication. Missing sentence punctuation is added only for display; existing punctuation, quotes and vocal-cue annotations remain intact. The authoritative transcript text and lifecycle events are not rewritten.

**History and scrolling.** Each native panel has its own ScrollViewer and follow intent. Follow resumes within 60 DIPs of the bottom. Reader movement invalidates pending follow work; scroll requests coalesce to at most one pending dispatcher callback per panel. Updates reuse the same two text runs and panel instances, do not steal focus and do not rebuild segment cards. Existing bounded 30-segment history is retained through silence, pause, same-source capture resets and model reload. Explicit clear and actual source removal/identity replacement retain their existing semantics. History is in memory only. The browser fallback retains its existing presentation and tested controls.

**Incremental recognition.** Initial partial windows now target 700 ms (previously 2.5 seconds). These are genuine growing PCM windows sent to Whisper, not animated completed text. Pacing uses measured inference duration, active-request elapsed time and source count; it backs off up to 2.5 seconds when overloaded. Only the latest queued partial per source is retained, finals keep priority, and final round-robin fairness and one global inference remain. An in-flight native request is not duplicated or assumed cancellable. The 15-second production freshness default and all Phase 5A retirement, attribution, epoch and error-health rules remain.

**Speech Recognition settings.** Controls now organizes Audio sources, Captions, Speech Recognition and Advanced/window/session behavior. A prominent active-model display distinguishes an actually loaded model from a candidate or a downloadable option. First-run users explicitly choose recommended Large-v3-Turbo download or an existing compatible local model, then confirm the corresponding action. No radio option is selected merely because a download exists. Busy/progress/error/ready states gate controls. One processing selector shows requested Auto/CUDA/CPU separately from the actual backend and its existing verification evidence. Detailed privacy/license/hardware explanations are expandable. Existing version-1 preference and runtime settings formats are retained.

**Audio levels.** Every 500 ms of received PCM, publish only the strongest 20 ms frame's RMS and derived dBFS, with a low-signal indication relative to the existing silence threshold. Controls can distinguish silence/very quiet playback from detected signal while retaining capture/inference failure information. Metadata is bounded, authenticated, generation-checked and discarded on resets/removal; raw PCM is neither retained for this feature nor recorded. No Windows device setting or Discord routing was changed.

## Validation

Performed in Linux with .NET SDK 10.0.404 and installed Chromium:

| Check | Result |
| --- | --- |
| `npm test` | PASS: 88 tests, zero failures/skips; includes 7 new Phase 5B tests and one new setup-state test |
| `dotnet run --project native/CaptionOverlay.Tests -c Release` | PASS: 120 assertions |
| `node test/native-overlay-session.js` | PASS: 12 native assertions plus authenticated session, commands, reconnect and shared clear |
| `python test/overlay-browser.py` | PASS: real multi-client browser/server integration and retained lifecycle regressions |
| `dotnet run --project native/RaymeLauncher.Tests -c Release` | PASS: 19 integrity/platform/diagnostic assertions |
| `dotnet run --project native/LoopbackCapture.Tests -c Release` | PASS: managed conversion, discovery, identity, activation cleanup and buffer policy |
| Normal and `RaymeDistribution=true` WPF cross-builds | PASS: zero warnings/errors |
| `native/PortablePackage.Tests` Windows-targeted cross-build | PASS: zero warnings/errors; execution requires Windows |
| Both workflows through actionlint 1.7.12; `git diff --check` | PASS |

The earlier partial cadence required two old tests to observe the expanded sequence: source replacement explicitly resolves the superseded partial before the final, and capture-offset ordering explicitly compares both finalized boundaries. Original isolation and ordering assertions remain and are strengthened. No tests were removed.

Added Windows GUI smoke assertions for native Settings hit testing after lock changes, lock icon/accessibility synchronization, continuous partial/final replacement, fixed panel identity, independent scrolling while rereading, low-level feedback, model-choice consent/loading/ready state and one processing selector. Existing styling, accessibility, source commands, tray recovery and clear checks remain. Portable UI automation now explicitly chooses the local-model option and checks Settings after lock changes.

The existing real Whisper speech integration now records actual audio-window and inference durations and the resulting adaptive interval. It requires an early 600–800 ms window and actual partial/final inference attempts while retaining the original final keyword recall, ordered phrase, attribution and history assertions. **These measurements were not run locally because no compatible model is installed here.** Windows CI must supply the actual results; a scheduled 700 ms window is not a claim of 700 ms visible-caption latency.

## Runtime reuse and delivery limits

Every native runtime identity input, Whisper/CUDA asset pin, build configuration, production Whisper/processing implementation, workflow/release gate and original Discord file remains byte-identical to the main baseline. The verified runtime key is unchanged for an identical runner/compiler/SDK/toolchain. The new backend module is included in the portable assembly allowlist and resulting integrity manifest. No CPU/CUDA rebuild was invoked locally; no checksums, freshness limits, real inference accuracy assertions or release safeguards were relaxed.

Windows run [#40](https://github.com/fluidhelix/Rayme-live-caption/actions/runs/43313203638) for `27c67b0` was verified through the repository web page: `runtime-inputs`, `build-test`, `caption-overlay`, `speech-integration` and `whisper-runtime` passed; portable acceptance was still running at that observation. The Actions API returned HTTP 403, but the public web summary was usable. The final terminal-punctuation/readiness follow-up requires its own matching run; do not treat the earlier commit's success as its result. Publication remains gated on full acceptance. A resulting ZIP is a physical acceptance candidate only after all existing Windows packaging, GUI/lifecycle and real inference gates pass. Linux cross-compilation does not validate native hit testing, rendered WPF geometry, actual scroll interaction, default endpoint availability, process-loopback capture, Large-v3-Turbo latency, RTX 4080 inference or physical multi-monitor behavior. Hosted tiny-model decode measurements must not be extrapolated to the production model or GPU. CPU may be unable to sustain subsecond captions.

Physical checklist: launch with the saved production model and verify default playback begins only after readiness; switch to two applications; toggle lock from Controls repeatedly and reopen Settings; reread A while B receives captions; verify silence and pause preserve text; switch CPU/Auto/CUDA and verify selected sources/pause restore with one Whisper runtime; test quiet playback feedback, unplugged device recovery, tray/click-through recovery and complete Quit. Record actual backend, hardware/model, observed word latency and any failure against the prerelease's commit. No stable release or unverified GPU performance claim is authorized by this report.
