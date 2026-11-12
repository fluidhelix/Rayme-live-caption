# Phase 5A — caption correctness and reliability

Baseline: `e311fa4f7b0fef4efd8b0ceb10b3b2678493b663`. Development branch: `fix/phase5a-caption-lifecycle`, intended draft PR base: `feature/windows-loopback-captions`. No merges or releases are part of this change.

## Failure mechanisms traced before implementation

1. `SourceScheduler` bounded each queue but checked neither the queued audio's age nor the completed result's age. A long inference request could publish an obsolete conversation and then drain obsolete work from both applications.
2. `Segmenter` could emit a partial after 2.5 seconds containing intermittent brief sounds, then reject the final because it contained less than 200 ms of voiced audio. That rejection emitted nothing. Empty inference responses and inference failures could similarly strand an earlier partial.
3. There was no segment retirement/revision contract. An older segment's completion could clear a newer partial; delayed events could revive discarded text. Native finalized-segment watermarks also rejected otherwise valid out-of-order finals.
4. Inference failure published `unavailable` followed by `reset`. Server, browser and WPF reset handling replaced that failure with waiting. Capture readiness could also obscure an inference failure.
5. Capture callbacks closed over a mutable source entry. A worker or segmenter retained across rapid pause/resume could reset or attribute data to its replacement. The server also lacked removal tombstones against late source events.

## Event and state contract

Sources retain the existing slot, identity and generation. Scheduled work adds a monotonic `revision` within that generation. Old generations, removed-source epochs and conflicting identities cannot update live state.

`segment-retired` carries `source`, `id`, `revision`, `terminal` and a short non-transcript `reason`:

- Terminal retirement closes the segment, including when an older revision's rejection arrives late.
- Nonterminal retirement clears only revisions through the stated revision; a later partial or eligible final remains possible.
- Retirement never deletes finalized history or a newer segment's partial. An obsolete partial cannot return after a newer segment has appeared.
- Empty responses, rejected segments, overload eviction, inference failure and freshness expiry all retire the appropriate work. Reset clears caption state without changing source health.

Server, browser and native state maintain bounded guards (128 segment entries per source plus watermarks). Snapshots include those guards and source removal epochs, so reconnect does not restore invalid partials. Existing history remains bounded to 30 entries, ordered by approximate capture time. Distinct out-of-order finals are accepted once. Source replacement/removal retains existing per-source history removal behavior; same-identity capture resets and pauses preserve finalized conversation.

The server reduces capture and inference health independently. Failure information survives resets, pause, capture readiness and inference retry; only explicit recovery of the failing component clears it. A successful empty inference response counts as inference recovery, but does not create text. States distinguish listening/no speech, capture activity, transcription/queued speech, falling behind, unavailable, recovery and stopped/paused. Both clients display the server's authoritative state; WPF shows readable labels and failure detail in the floating window and Controls.

## Freshness and latency

The default maximum live caption age is **15,000 ms**, configurable through `CAPTION_MAX_AGE_MS` (1,000–120,000 ms). The portable launcher's existing environment allowlist admits this optional setting; no user setting or inference configuration is rewritten.

Age starts at the approximate **end of the audio window**, so a valid 15-second utterance is not rejected simply because its first sample is old. At enqueue, wall-clock audio age is converted to a monotonic elapsed-clock deadline. Later wall-clock adjustments cannot prolong or prematurely expire that work. Age is checked before inference and before publication. Displayed partials also expire on that deadline, including while another source's inference is stalled. Queue waits exceeding three seconds display a falling-behind status.

Overload or expiry reports a visible source warning instead of silently losing speech. A fresh successful result clears the warning. No finalized history is automatically hidden. Existing global single-request inference, final priority, bounded queues and round-robin final fairness remain. In-flight native inference is allowed to finish and its obsolete result is discarded; no parallel Whisper request is started to circumvent it. Fresh workloads gain no intentional delay. Under overload, obsolete speech is dropped instead of presented as current conversation, reducing the backlog after the active request completes.

The PCM pipe still has no WASAPI QPC timestamp header. Audio timestamps use the existing first-block arrival anchor plus sample offsets, so cross-application timing and the initial age estimate remain approximate. This change does not claim sample-accurate timing or lower native model inference duration.

## Files changed

- Backend lifecycle: `windows-captions/caption-lifecycle.js` (new bounded ledger and health reducer), `sources.js`, `audio.js`, `capture.js`, `server.js`, `main.js`.
- Clients: `windows-captions/overlay.html`, `native/CaptionOverlay.Core/SegmentLedger.cs` (new), `CaptionState.cs`, `native/CaptionOverlay/OverlayWindow.cs`.
- Portable inclusion/configuration: `distribution/assemble.js` includes the new backend module; `native/RaymeLauncher/Program.cs` adds only the freshness environment variable to its allowlist.
- Regression coverage: `test/caption-lifecycle.test.js` (new), `test/phase2.test.js`, `test/native-overlay-session.js`, `test/overlay-browser.py`, `native/CaptionOverlay.Tests/Program.cs`.

No Whisper source, flags, CUDA configuration, runtime identity, checksums, production model, native audio capture, default-device selection, authentication, private handoff, shutdown supervision, release gates, dependency versions or original Discord files changed.

## Validation performed in Linux

| Check | Result |
| --- | --- |
| `npm test` | PASS: 78 tests, zero failures/skips (63 existing plus 15 new lifecycle tests) |
| `dotnet run --project native/CaptionOverlay.Tests -c Release` | PASS: 74 assertions |
| `node test/native-overlay-session.js` with .NET on PATH | PASS: 12 assertions; actual authenticated Node/WebSocket/native client, reconnect, controls and cross-client clear |
| `python test/overlay-browser.py` | PASS: real Chromium, two clients, captions, controls, reconnect, retirement revisions, failure persistence/recovery and replacement |
| `dotnet build native/CaptionOverlay -c Release -p:EnableWindowsTargeting=true` | PASS: zero warnings/errors (cross-compilation, not GUI execution) |
| `dotnet run --project native/RaymeLauncher.Tests -c Release` | PASS: 19 integrity/platform/diagnostic assertions |
| `dotnet run --project native/LoopbackCapture.Tests -c Release` | PASS: managed audio conversion, discovery, identity, activation cleanup and buffer policy tests |
| `actionlint` 1.7.12, both existing workflow files | PASS |
| `git diff --check` | PASS |

The 15 new Node tests use controlled inference promises and deterministic clocks for active/queued expiry, simultaneous sources, fair backlog pruning, displayed-partial expiry, queue warnings, wall-clock changes, cancellation, duplicate finals, source replacement/shutdown, worker/segmenter epoch isolation, out-of-order results, reconnect guards and persistent errors. The rejected-segment test reproduces the default segmenter thresholds with intermittent sounds; it does not change eligibility. The existing fairness test retains its exact caption assertions and additionally requires all 32 evictions to emit retirement events. Existing security, launcher, packaging, audio and inference assertions remain intact.

Native state tests cover retirement revisions, newer-partial preservation, terminal closure, out-of-order finals, snapshot/tombstone replay, health labels and failure-preserving reset/pause. Browser and native WebSocket session regressions exercise these events through the production server rather than only calling a presentation reducer.

## Runtime reuse and CI boundaries

All files listed by `distribution/runtime-bundle.js` in `inputFiles`, the `whisper`/`cuda` asset pins, identity implementation and both workflows are byte-identical to the baseline. Their source-input fingerprint is `4e211fffd95751e7834cecd568e6241b935d078a5014fb7d01bbcda511178d8f` (this is not the complete compiler-dependent runtime key). **The Whisper runtime cache key remains unchanged for the same runner image/compiler/SDK/toolchain.** Actual toolchain changes continue to change the key as required.

This `fix/**` branch does not match the workflow's push branches. A PR uses the existing trusted exact-key native artifact consumer; its CPU/CUDA runtime producer is explicitly skipped. If the matching trusted artifact is missing or the hosted toolchain has changed, that consumer must fail/wait rather than compile an unreviewed replacement. Existing lightweight/CPU speech and Windows packaging acceptance coverage is retained. PR release gates cannot publish. No full CPU/CUDA runtime build was invoked locally.

## Remaining verification

Windows CI and complete portable ZIP acceptance for this commit are not yet verified. Packaged WPF GUI rendering, physical multi-monitor interaction, real process-loopback capture, production Large-v3-Turbo transcription and NVIDIA hardware inference were not executed in this Linux environment. No claim of visual, audio-device, CUDA or production end-to-end success follows from the synthetic lifecycle tests. Real Whisper accuracy tests, hashes and release gates have not been weakened.

For Windows review: connect two applications, speak while deliberately delaying inference, verify the falling-behind/expiry warning, then verify fresh captions recover. Exercise rapid pause/resume, source replacement and capture/inference failure while a partial is visible. Confirm the old partial disappears, the meaningful failure remains until its component recovers, other-source partials and finalized conversation remain correct, and reconnect reproduces the same state.
