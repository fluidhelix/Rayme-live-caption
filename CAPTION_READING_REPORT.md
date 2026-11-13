# Caption reading: local implementation for review

The focused audit follow-up is documented in [CAPTION_READING_AUDIT_FOLLOWUP.md](CAPTION_READING_AUDIT_FOLLOWUP.md). The results below record the original `0163faf` implementation; the follow-up adds keyboard/command selection protection, equivalent source-name accounting and stronger pending Windows geometry coverage.

Baseline: `e69682b7b6893fab09f3f3033d8a7d851be4b6c8` (`main`, rc.44). Remote main was fetched before implementation and matched the baseline. This work is **local only**: no push, Windows workflow dispatch, tag or release publication is authorized or performed. The final local commit is available from `git log -1`; this report is part of that commit.

## Verified mechanisms and limits of the diagnosis

1. The old `TextPosition.IsAppend` rule followed a growing unfinished result only when its text started with the previous result. A corrected prefix with additional words failed that rule, while a new segment passed. This directly explains the code path in which continuous recognition can stop following and the next utterance resumes it. The rule has been removed; visibility of the actual latest line now determines whether following needs a scroll.
2. The old native `ScrollChanged` handler inferred manual navigation from changes with zero extent/viewport delta. WPF can report the same kind of event for programmatic scroll restoration or extent clamping. Those events changed `ScrollFollow.Revision` and could invalidate scheduled work. Only explicit reader input now changes reader intent.
3. The scheduler expires a displayed **partial** at its audio freshness deadline, even if the text looks like a complete sentence. Time spent waiting/inferencing counts toward the 15-second deadline, so the visible lifetime can be much shorter than 15 seconds. A missing, failed or stale final then leaves no finalized version. Existing fake-clock tests reproduced partial removal before the change; new tests reproduce those transitions with retained unfinished text. No timer expires finalized history. A 24-hour fake-clock test verifies final-history survival without further speech/events.
4. Shared `slice(-30)` / `TakeLast(30)` pruning let either source evict the other's conversation. Extent shrink, wrapping and invalidated anchors can then move the viewport. The old anchor fallback used an ordinal from the old list to index the pruned list, which can identify a different segment. Stable surviving keys now determine fallback.

These are verified code/state mechanisms. They do not prove which individual lines in the user's physical observation were finalized versus provisional, nor reproduce that exact Windows timing sequence. Native geometry coverage is compiled but not executed here. No actual audio or transcript is logged to resolve that uncertainty.

## Event/state contract

- Final captions remain authoritative and immutable under retirement. They have no wall-clock expiry. Clear, source removal/replacement and bounded cleanup remain valid removal operations.
- The existing `segment-retired` event gains an optional `retain: true`. This retires the live partial under the same revision/terminal guards and moves the **already displayed** matching draft into retained history with `final: false, unfinished: true`. The event carries no extra transcript copy. If Clear has already removed the partial, a later timer cannot reconstruct it.
- Clear History is not recognition cancellation: active/queued jobs and capture continue. A still-fresh completion may be published after Clear and enter the new replay history. This existing behavior is unchanged; the audit follow-up explicitly tests in-flight partial and final completion after Clear.
- Freshness/backlog skips, inference failure, empty recognition and displayed-partial expiration request retention. A newer segment without an older final retains the superseded draft. A capture generation change for the same application retains the visible draft as unfinished while invalidating all old live/inference work. Application identity replacement/removal still clears its history.
- Rejected insufficient audio retains nothing. An explicit terminal rejection without `retain` also removes only the matching unfinished record, never finalized history or a newer segment.
- A fresh accepted partial/final replaces the retained record with the same source/generation/segment identity. Nonterminal retirement allows a genuinely newer revision; terminal retirement and generation/identity guards still reject late results. Old results are never published merely because their useful draft is retained.
- The global single-inference scheduler, fairness, queue priorities, 15-second default freshness, monotonic clock and cancellation behavior are unchanged. No extra capture, recognition job or protocol authority is created.

The native window uses its reserved badge space for LIVE or DRAFT. Retained unfinished runs are underlined and have an explanatory tooltip; body size/weight, wrapping width and header geometry do not change. No final punctuation is invented for drafts. The browser labels retained unfinished rows too. Final success replaces the same native run rather than appending a duplicate.

## Retention and resource bounds

Every source independently retains at most **200 segments**, **65,536 UTF-16 text characters**, and **131,072 conservative encoded-text/metadata budget units**. The byte estimate counts escaping/Unicode conservatively and adds per-record metadata overhead. Unfinished records share that budget; the freshest chronological tail is retained. A, B, whole-device and legacy state use the same policy. The existing native 4,000-character per-result limit is applied consistently at the server, with surrogate-safe truncation.

This usually allows about 200 short utterances per application instead of 30 shared entries, while long or heavily escaped text reaches a character/byte limit earlier. The 128 KiB allowance is a retained-data budget, not a claim about the total WPF process working set. There are normally at most two enabled application sources or one device source. A synthetic three-source test checks independence across modes; an authenticated Node/native test replays 400 entries. The 2 MiB native message limit, server slow-client protection and bounded revision ledgers remain unchanged.

`history-policy.js` is shared by server and browser. `HistoryPolicy.cs` mirrors its budgets and accounting; regressions check count, long-text and escaped-text boundaries on both implementations. The new module is included and required by the portable package allowlist. Nothing is persisted to disk, and no new preference/configuration control is added.

## Scrolling and native document behavior

- Wheel, navigation keys, scrollbar interaction and text selection begin reader navigation. Position is sampled after input/layout; returning within the existing 60-DIP bottom threshold resumes following. Automatic scroll/layout events never independently alter that intent.
- While following, any content revision can reveal an obscured latest line: prefix extension, rewritten prefix, shrink, punctuation or final correction. Already visible text is not unconditionally sent to the bottom. Status, LIVE/DRAFT classification and silence do not request document scrolling.
- While rereading, stable segment keys and corrected text offsets preserve a surviving word's screen position. Removed anchors fall back to the nearest surviving later key, then a preceding key, then the retained beginning. An entirely pruned reading position cannot be preserved; it recovers to available text without restoring obsolete content or forcing live-follow mode.
- Resize and font reflow preserve reading anchors while rereading. Following reflow releases temporary slack and keeps the current tail visible; enlarging reveals retained earlier lines. Window size never changes retention limits. Combined/Detached views continue reparenting the same per-source controls and reader state.
- Temporary end slack is limited to one viewport, rather than allocating offset-sized blank pages. It is reduced by later updates and released on following reflow or manual return to the bottom. Explicit clear/source replacement resets old anchors and pending reader input.
- Per-segment native runs are reused; unchanged text is not rebuilt. Header metadata no longer concatenates the entire retained transcript on every status update. Undo remains disabled, text remains literal/selectable, and no focus call is added to caption processing.

## Changes

- Backend: `windows-captions/sources.js`, `server.js`, new `history-policy.js`; browser `windows-captions/overlay.html`.
- Native state/presentation: `native/CaptionOverlay.Core/CaptionState.cs`, `Presentation.cs`, new `HistoryPolicy.cs`; renderer `native/CaptionOverlay/TranscriptPanel.cs`.
- Native synthetic UI tests: `OverlayWindow.DesignSmoke.cs`, `OverlayWindow.Smoke.cs`; native state/session tests in `native/CaptionOverlay.Tests/Program.cs`.
- Backend/browser/package tests: new `test/caption-reading.test.js`, existing lifecycle, WebSocket session, browser, Windows protocol and distribution tests.
- Packaging/docs: `distribution/assemble.js`, `distribution/README.txt`, `WINDOWS-CAPTIONS.md`, this report and a follow-up pointer in `OVERLAY_UI_REPORT.md`.

No change to native runtime build identity, source pins, CUDA settings, Unicode manifests, capture/endpoint architecture, Whisper requests, original Discord files, preferences, application theme, Controls or tray/lifecycle implementation.

## Validation

Verified locally on Linux:

| Check | Result |
| --- | --- |
| Full Node suite | 99 passed, zero failures/skips; lifecycle/fairness/security/integrity/release tests retained |
| Native state/settings/retention/reader-intent suite | 177 assertions passed |
| Actual authenticated Node/native WebSocket integration | 16 assertions passed, including 400-entry reconnect, unfinished classification, fresh-final replacement, origin/token rejection and shared Clear |
| Actual Chromium browser integration | Passed; independent 200-entry retention, unfinished/final replacement, reload, cross-client clear, original controls/source protections |
| Launcher integrity/platform/diagnostics | 19 assertions passed |
| Managed audio conversion/discovery/identity/cleanup/buffer tests | Passed |
| WPF development and distribution cross-builds; portable acceptance harness build | Passed with zero warnings/errors |
| Both workflows through actionlint; whitespace validation | Passed; workflows themselves unchanged |
| Native runtime identity regression | Reproduces the unchanged verified Windows key; no CPU/CUDA compilation invoked |

New deterministic backend cases cover prolonged silence, deadline expiration, stalled/stale finals, failure/empty recognition, useful superseded drafts, explicit rejection, source generation change/replacement, out-of-order revisions, clear/timer races, all retention budgets, reconnects and Unicode truncation. Existing small-history assertions are replaced by stricter per-source/budget assertions; finalized-history protection remains separately asserted when unfinished records are added.

New actual-WPF smoke cases measure latest-line visibility through continuous prefix/non-prefix updates, shortened/final text, independent reader state, manual bottom return, selected text, silence without events, pruning at the bottom and while rereading, small/large cycles, font reflow, removed anchors, classification-only retirement and bounded slack. Combined/Detached assertions now also check a surviving reading word after reparenting. Diagnostics contain only bounded counts/geometry, not text/audio.

**Not verified here:** executing WPF geometry/UI tests. A direct local smoke launch stopped before application code because `Microsoft.WindowsDesktop.App` is unavailable on Linux (exit 150). Compiling those tests is not a GUI PASS. No Windows CI, packaged Windows acceptance, real-model inference, audio-device or GPU test was run for this local commit. Existing mandatory tests/gates remain for later explicitly authorized Windows validation.

## Review and physical acceptance

Review the optional retirement field and same-identity reset contract, mirrored budget implementation, reader-input lifetime/dispatcher cancellation, native anchor mapping after wrapping, and one-viewport slack bound. Windows GUI smoke is the next automated gate once authorized.

On physical Windows, exercise long uninterrupted Large-v3-Turbo speech with prefix corrections, pause/silence after useful drafts and finals, wheel/keyboard/thumb rereading, selection/copy while the other source speaks, long-history pruning, repeated small/large resizing and font changes, and Combined/Detached moves across mixed-DPI monitors. Verify that old text remains reachable, unfinished markers are clear, returning to the bottom resumes follow, Settings/tray/Quit remain usable, and the single existing backend/Whisper process is preserved. Long-session rendering cost and the exact reported physical disappearance/jumping sequence remain unverified.

Expected latency: no recognition cadence, model configuration or freshness-window change, and no additional inference work. UI follow is settled after WPF layout; increased retained history adds bounded document/layout work whose physical gaming impact still needs measurement. This work does not claim improved Whisper throughput or verified CUDA acceleration.
