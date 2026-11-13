# Approved overlay implementation

Baseline: `d7365b23352264d51260951b34cdb21813ded10e` (`rc.41`), verified against `origin/main`. The user approved the existing design, then required neutral charcoal throughout the application and preservation of the live 16–60 text-size slider. No second mockup stage was performed. The approved reference is in `docs/APPROVED_OVERLAY_DESIGN.md`.

## Implementation

- Black (`#000000`) caption paint with existing 70–100% background alpha, opaque neutral text, name-only source titles and original `#80DFFF` / `#FFD68A` / `#CBB9FF` defaults. Existing custom colors and version-1 preferences survive.
- One enabled application fills the existing window; A+B split it 50/50; disabled sources have no empty panel. Whole-device remains one panel. Enabled-but-unavailable or paused sources keep their panel and meaningful health.
- Explicit Detach is enabled only for both application sources. Two presentation-only peer windows share the one existing SessionClient and CaptionState. The same transcript controls move between parents, preserving their document, selection, anchors and scroll intent. Each window has independent placement, monitor recovery, size and lock. Reattach restores the combined placement/lock; source removal automatically consolidates. Detach mode is not persisted or automatically reactivated.
- No layout action sends an audio/model command or launches a second overlay process. Programmatic view closure bypasses user Quit; backend EOF/user Quit closes all peers. The central hidden host retains its HWND and tray registration. Controls is re-owned to a visible window, including when that window is locked. Group Hide/Show and tray recovery remain.
- Continuous selectable, read-only native text uses stable per-segment inlines in one paragraph. No transcript markup is interpreted, no extra cards or paragraph breaks are introduced, and undo storage is disabled. Header heights and scrollbar gutter are fixed. Actual text/glyph areas select normally; blank caption surfaces and headers drag using the normal WPF threshold while unlocked. Controls, scrollbar thumbs/tracks and resize grips are excluded.
- The source color picker offers saturation/brightness, hue, HEX/RGB fields, a neutral-body sample, validation/contrast warning, Restore default, Cancel and Apply. It updates only the selected preference, using existing atomic persistence. Existing presets remain available. Text size remains 16–60 with live numeric feedback; opacity remains live and persistent.
- A shared neutral theme covers Controls, guided Speech Recognition, the native picker, text context menus and the WPF tray flyout. The established WinForms tray fallback remains with charcoal background, selection/pressed colors, separators and keyboard navigation. Original Rayme icon bytes are unchanged.
- Rayme-owned startup/recovery prompts now use a shared charcoal WPF dialog. The launcher therefore targets the same pinned self-contained Windows Desktop framework as the overlay; this adds bundled launcher framework size but requires no user runtime installation. Diagnostic mode remains non-modal. Windows-owned file pickers, apphost/loader and security prompts follow Windows; no undocumented OS theme hooks or device settings are used.
- The browser fallback keeps its tested behavior and receives neutral CSS colors only.

## Caption movement mechanism and correction

In rc.41, every final/partial string change could request ScrollToEnd. Finalizing rebuilt the joined final prefix and cleared the partial tail, possibly shrinking or rewrapping the document. A native ScrollViewer can clamp its offset after extent shrink. Automatic scrollbar visibility could change wrapping width, and wrapping status text in an Auto row could change viewport height. LIVE already used Hidden and reserved its slot.

The update separates genuine appended speech from final corrections. Only append/growth while following can scroll to the bottom. Before changing native runs, it captures a segment/text-position anchor and screen displacement. After layout, corrected positions map through unchanged prefixes/suffixes; removed positions fall back to surviving segments. Temporary end padding prevents premature offset clamping and is reduced to the space needed by that anchor. Reader navigation invalidates pending follow operations; status and silence do not mutate the document or schedule scrolling. Accurate final text, retirement, source-generation guards and bounded backend history remain authoritative.

Windows GUI smoke includes a hermetic legacy-policy probe and current renderer tests, with bounded geometry-only traces (`caption-geometry.json`). These use synthetic captions and log offsets/extents/viewports/width, not real transcripts or PCM. They can demonstrate the mechanisms and the corrected synthetic behavior; they do not establish every cause of the original physical-machine screenshots without on-machine acceptance.

## Local verification (Linux)

- 88 Node tests: PASS, no failures/skips.
- 148 native state/settings/color/layout assertions: PASS.
- Actual authenticated Node↔native WebSocket suite: PASS, 12 assertions; reconnect and shared Clear History retained.
- Real Chromium multi-client suite: PASS after neutral fallback CSS changes.
- 19 launcher integrity/platform/diagnostic assertions: PASS.
- Managed audio conversion/discovery/process identity/activation/buffer policy tests: PASS.
- Overlay normal/distribution, launcher and packaged UI automation Windows cross-builds: PASS with zero warnings/errors. Actual self-contained win-x64 overlay and launcher publishing also passed locally. These checks are compilation/publishing, not GUI execution.
- Both workflows through actionlint and git diff --check: PASS.

New native GUI coverage exercises fixed wrapping geometry, partial-to-final shrink with a surviving word at the same screen position, silence/no-scroll, selectable text, dynamic layouts, two real HWNDs, independent resize/lock, Settings ownership, native color Apply/Cancel/Restore, charcoal prompts, reattachment and parent EOF while detached. Production builds exclude synthetic UI probes. The workflow reports bounded native smoke failure details as an annotation; no acceptance gate is relaxed.

## Runtime, CI and acceptance boundaries

The first Windows run of the approved UI (`43430903849`) passed backend/browser and real speech integration but stopped on two checks. Diagnostic run `43431477180` established the causes: WPF omits UI Automation's TransformPattern from the locked frameless window, and Windows-2022 jobs were assigned different rolling images (MSVC 19.44.35230 versus 19.44.35229). The lifecycle test now checks the actual native resize style and performs/observes a documented SetWindowPos resize of the other window. It retains the independent-lock, independent-size, Controls, reattachment and EOF assertions.

`distribution/verified-runtime.json` records the already validated rc.41 compiler identity and unchanged native key `whisper-win-x64-v1-2b5c8b65eb992f9cf916f90ff923507b98837591885a2b9ff9aa2f97696df45e`. Resolution recomputes every native source/asset input against that identity, then the existing bundle verifier checks all files, executable help and embedded Unicode manifests. Reusing binaries does not require their compiler to be installed on the consuming runner. If a bundle must be compiled, the actual installed toolchain must still match the pinned identity; source/toolchain changes require an explicit validated reference update. No identity algorithm or pinned native build input is changed. New regressions reject reference/source/compiler tampering and reproduce the existing Windows key. These follow-up checks passed locally; their Windows results must be checked in the subsequent run.

Pinned Whisper/CUDA sources/assets/configuration, runtime identity inputs, inference/capture/scheduling/default-device logic, authentication/private handoff, original Discord files and Rayme icon remain unchanged. The native runtime key is unchanged for an identical runner/toolchain. Existing verified native artifacts remain reusable; no CPU/CUDA build was invoked locally. Only the launcher's existing pinned framework graph changes to Windows Desktop. Required real inference, ZIP integrity, clean portable startup, Unicode paths, process cleanup and prerelease gates remain.

Windows CI and prerelease results must be checked for the final commit; they are not claimed from Linux builds. Physical mixed-DPI/monitor removal, long-session selection/scroll behavior, the exact reported user's speech→silence sequence, production Large-v3-Turbo latency and RTX 4080 inference remain acceptance work. The release stays a prerelease, never stable.

Physical acceptance: restore the existing settings; check exact original/custom title colors, black alpha and font-size persistence; exercise A-only/B-only/both/fallback; detach only by clicking, independently move/resize/lock across monitors, reopen Settings, reattach and disable a source; scroll back and select/copy while the other source speaks; inspect partial/final corrections without a viewport jump; unplug a monitor, recover via tray, Hide/Show, mode switch and Quit completely. Confirm one Whisper/backend and no orphaned peer windows.
