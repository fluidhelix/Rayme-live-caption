# Accessibility, five applications and continuous transcript refinement

Date: 2026-11-15. Baseline: `22fbbb2c77a51719a5026d464f4da62a74c6b8f1` (rc.56).
Feature branch: `feature/accessibility-five-applications`.

**Local implementation and validation only. No Actions workflow was run or dispatched, no PR was created, nothing was pushed, main was not changed, and no tag or release was created. Both workflow files and all existing release guards are unchanged. Windows runtime validation is NOT RUN.**

## Seven requested changes

1. **Unfinished text appearance.** Native caption Runs no longer receive underlining. Unfinished/final classifications, source/segment identities, text, annotations, retention and correction rules remain intact. Short recognized replies continue to retire as unfinished history rather than being invented as finals. The tooltip can still explain unfinished recognition when deliberately inspected.
2. **Five applications with progressive controls.** A is the only initial application card. Add Application inserts the next missing slot B–E above whole-device fallback. Optional cards can be removed; five is the maximum. New cards do not create capture workers. Active-card removal waits for the backend removal acknowledgement, and a send failure leaves the active source visible for retry. Colors/settings expand only for added slots. SourceManager and authenticated server command validation accept A–E and retain whole-device mutual exclusivity. The browser fallback also supports progressive A–E selection.
3. **No LIVE/DRAFT caption badge.** Its label, container and exclusive reserved space were removed. Internal recognition state remains available to lifecycle/presentation code and tests; no replacement speech animation was added.
4. **Invisible scrollbars.** Native transcript scrolling uses ScrollBarVisibility.Hidden, without a scrollbar gutter. The RichTextBox, ScrollViewer, selection, keyboard commands and mouse-wheel handlers remain. Existing thumb-route tests are preserved by temporarily exposing a bar in isolated test panels only; production panels remain hidden. Wheel/keyboard tests exercise hidden bars.
5. **Stable availability.** Each application title has a green Available indicator during waiting/capturing/transcribing. Unavailable, recovery, genuine overload, pause and connection loss remain explicit. Routine speech transitions do not change the visible status. Critical notices are scoped to the affected caption window; settings retain detailed source diagnostics. Toolbar/icon/availability updates are cached when their presentation does not change. Closing a selected process still follows the existing terminal unavailable/manual-reselection path; capture.js was not changed.
6. **Contextual icons.** Small native vector equivalents of link-2, unlink and panels-top-left replace textual header Attach/Detach buttons. One unambiguous target attaches directly; several targets open a compact dark WPF menu with application names and slot identifiers; a pair separates in one click. Lock/Settings, keyboard accessibility and tray recovery remain. No icon library/runtime dependency was added.
7. **Pruning layout context.** The renderer still contains one continuous Paragraph of keyed Runs, with natural wrapping and copying. On measurable prefix pruning, it preserves the first surviving glyph's existing horizontal origin using a bounded Paragraph.TextIndent. Deleted Runs, old segment text and removed-anchor text are released. No hidden old transcript, spacer Runs, extra paragraphs or accumulating blank pages are used.

## Architecture, grouping and saved layouts

`CaptionOverlay.Core/CaptionLayout.cs` owns presentation groups only. It produces groups of one or two active slots; every slot occurs once. Default A+B is vertical, and newly added C/D/E are standalone. Previously selected pairings/order can be restored from preferences. The host renders at most two equally stacked Grid rows per window. It reuses the same TranscriptPanel/RichTextBox for each source across reparenting, preserving its history, selection and reader intent. There remains one SessionClient, one shared backend scheduler and one Whisper runtime.

For example, with A+B paired and C standalone:

- C choosing B creates A+C, with C in the old lower row; B receives C's exact standalone physical bounds and lock state.
- C choosing A creates C+B, with C in the old upper row; A receives C's previous standalone bounds.
- Two standalone sources use their saved attached profile and remembered vertical order; if none exists, the target placement seeds the new pair.
- Separating a pair restores each source's independent standalone placement, size, monitor and lock.

Pair placements are distinct from DetachedA–E. Up to twenty ordered pair profiles and ten pair-order entries are bounded; current membership and visible card slots are persisted. Old version-1 A/B colors, combined Placement/Monitor, DetachedA/B and custom preferences migrate additively. The legacy attached lock/placement is materialized before startup switches to whole-device mode, so the mutable active-window lock cannot overwrite it. Preferences retain value equality across serialization. The existing 16-KiB settings-file limit is retained; oversized saves fail before overwriting a valid file.

A single remaining member of an attached pair can continue using that attached placement profile. Merely disabling its partner does not overwrite the saved detached position. Detach and Attach switch between the separate profiles. Removing extra sources leaves the familiar A/B arrangement available. Monitor recovery reuses existing documented Windows placement/DPI APIs; a loaded peer is recovered from its current physical bounds rather than forcing its old saved monitor during a move.

Source windows can be hidden without removing capture; explicit tray Quit Rayme retains the secure owned-backend shutdown mechanism. A native close hides when tray recovery is available; if it is unavailable, the established controlled quit path prevents an inaccessible orphaned session. Backend EOF still closes owned windows. Session tokens, transcripts and process identities are not stored in presentation preferences.

## Discovery without automatic reconnection

Opening Controls requests the existing authenticated refresh command. Opening a selector requests/coalesces refresh too; compact per-card refresh icons and the existing manual session Refresh remain. Outstanding requests are coalesced, with a bounded retry interval on missing replies. Discovery updates are applied while control events are suppressed.

A refreshed list retains the selected exact process identity. If that identity is absent, it is shown as unavailable rather than replaced by a new PID with the same name. Only a deliberate user selection sends configure for the replacement identity. Refresh itself never reconfigures capture. Native and actual browser/WebSocket tests cover this separation.

## Shared inference fairness

Five-source stress tests reproduced starvation under the old unconditional global final priority. With three sustained final producers, waiting partial-only sources received no turn. A second experiment showed that sharing only the final cursor could repeatedly grant the same partial producer.

Final priority is now bounded to a source-count burst while a partial waits. Partial grants have their own round-robin cursor. There is still exactly one model request, the same bounded per-source queues, generation/revision checks, final watermarks, freshness policy and overload statuses. Existing final-priority/round-robin tests remain passing. These are deterministic scheduling tests, not a claim that five simultaneous real-model conversations meet a latency target.

## Pruning cause, solution and evidence

Removing text from the beginning of one wrapped paragraph changes its initial line fill. Following words can therefore shift horizontally and acquire different line breaks. A vertical reading anchor alone can restore a glyph's Y position while its X position and surrounding wrapping have changed. The inspected renderer removed prefix Runs and normalized the new head to column zero, which permits exactly that reflow.

Before deleting a measurable prefix, the new code obtains actual WPF glyph rectangles for the current head and first surviving content. It replaces (does not add to) a scalar first-line continuation indent. Keeping that horizontal origin retains the old line-fill context; the existing vertical-anchor restoration operates unchanged. Font/width changes legitimately reflow and reset the continuation; resets, replacement or an entirely new retained prefix also clear it. If usable native layout does not exist yet, the renderer falls back to normal flow rather than guessing geometry or keeping deleted content.

The local Chromium experiment models the same continuous paragraph using actual glyph rectangles. Unprotected prefix removal produced **37 horizontal movements**. The protected variant preserved two surviving measured glyph positions/relative wrapping across **270 pruning operations**, at three widths and three font sizes, while removing each old DOM node. This verifies the layout mechanism and strategy in Chromium; it does **not** establish native WPF geometry success.

New native smoke probes assert actual X and Y at the existing one-DIP tolerance across repeated pruning, variable widths/fonts, and partial/final corrections. They also assert the 200-segment bound, removed text/Run release, absence of pruned-anchor text and one continuous selectable Paragraph. Those probes compile but are **NOT RUN** on Windows yet.

Tradeoff: a continued first line can begin at its old horizontal position rather than flush left. This is one bounded line-context value, not retained text or an accumulating blank gutter/page. Fresh layout, width/font changes, unavailable geometry and complex text shaping may legitimately require normal reflow. Physical Windows/Segoe/mixed-DPI/bidirectional-text behavior remains an acceptance requirement; perfect stability is not claimed.

## Auto-follow protection

Compared against the accepted baseline, the following code is byte-for-byte unchanged:

- Core ScrollFollow (including explicit-bottom resumption and upward-motion protection).
- ReaderNavigating / ScheduleReaderPosition and their dispatcher revision guard.
- QueueRestore / Settle / RestoreAnchor, including existing end-slack bounds and live-tail visibility checks.

The pruning change supplies paragraph line context before those existing mechanisms run. It does not alter their thresholds, reader-intent policy or blank-space limit. Existing short-response, routed input, selection, resize, correction, dispatcher-interleaving and pruning assertions remain. Tests that need thumb geometry temporarily expose only isolated test scrollbars, then restore Hidden; no production scrollbar is made visible.

## Local validation

| Check | Result |
| --- | --- |
| Full Node suite, including package integrity/release guard and original Discord boundary tests | **111 PASS**, no failures/skips |
| Native core/state/preferences/grouping/history suite | **296 assertions PASS** |
| Actual .NET ClientWebSocket against production authenticated Node server | **22 assertions PASS**, including five-source live history and reconnect |
| Existing Chromium source/control/history integration | **PASS** |
| New progressive five-application Chromium integration | **PASS** |
| Chromium prefix-pruning geometry experiment | **PASS**, 270 protected prunes; explicitly not WPF |
| Launcher integrity/platform/diagnostic suite | **19 assertions PASS** |
| Existing managed audio conversion/discovery/identity/cleanup/buffer suite | **PASS**; no physical audio claim |
| WPF development and distribution cross-builds | **PASS**, zero warnings/errors |
| Self-contained Windows x64 WPF cross-publish, locked dependencies | **PASS**; executable not run on Windows |
| PortablePackage.Tests cross-build | **PASS**, zero warnings/errors; acceptance execution NOT RUN |
| Both unchanged workflows through local actionlint and whitespace checks | **PASS** |
| Baseline comparison for runtime inputs/workflows/Discord/audio capture/Whisper | **Unchanged** |

Commands used (SDK 10.0.404; restore/publish uses the existing lock files):

```text
npm test
dotnet run --project native/CaptionOverlay.Tests -c Release
node test/native-overlay-session.js
python test/overlay-browser.py
python test/five-application-browser.py
python test/pruning-layout.py
dotnet run --project native/RaymeLauncher.Tests -c Release
dotnet run --project native/LoopbackCapture.Tests -c Release
dotnet build native/CaptionOverlay -c Release
dotnet build native/CaptionOverlay -c Release -p:RaymeDistribution=true
dotnet build native/PortablePackage.Tests -c Release
dotnet publish native/CaptionOverlay -c Release -r win-x64 --self-contained true -p:RestoreLockedMode=true -p:RaymeDistribution=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true
```

## NOT RUN and Windows acceptance checklist

- **GitHub Actions/Windows CI: NOT RUN by explicit instruction.** No run, PR or validation URL exists for this feature branch.
- **Native Windows GUI and routed-input/geometry execution: NOT RUN.** Run the existing packaged synthetic smoke after separately authorizing Windows validation. It now covers progressive native controls, pairing menus/icons, exchanges, repeated saved-layout transitions, retained short text without decoration, stable status, hidden scrollbars and X/Y pruning geometry.
- **Complete portable ZIP/native launcher/process lifecycle on Windows: NOT RUN.** Keep existing acceptance and integrity gates, Unicode/spaced paths, repeated Quit/Hide/control-close and no-orphan assertions.
- **Real CPU Whisper inference locally: NOT RUN.** An existing Linux executable is available, but no genuine model is installed. The pinned tiny.en download attempt was denied by the network proxy (HTTP 403). No dummy model or mock replaced the required integration assertion.
- **Real WASAPI audio, NVIDIA CUDA/RTX 4080, multi-monitor mixed-DPI/unplug behavior, and sustained Large-v3-Turbo/five-way inference: NOT RUN.** Required real-machine acceptance remains separate from synthetic scheduling/UI tests.

Whisper native identity is unchanged:
`whisper-win-x64-v1-2b5c8b65eb992f9cf916f90ff923507b98837591885a2b9ff9aa2f97696df45e`.
No Whisper CPU/CUDA source, binary, compiler configuration, checksums, license or model pin was changed or recompiled. No release/package gates were disabled. The local cross-published overlay is a compilation artifact, not an approved friend-ready release.

Review the feature-branch implementation and authorize Windows validation separately before merging or publishing.
