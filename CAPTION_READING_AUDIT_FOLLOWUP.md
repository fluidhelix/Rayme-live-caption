# Focused caption-reading audit corrections

Parent implementation: `0163faf349d8c7cffc98e1001e2e370558fcd88a`. The independent audit found no Critical/High defect and requested two localized corrections plus stronger Windows input/geometry coverage. This follow-up remains local for review: no push, workflow dispatch, tag or release.

## Keyboard selection and explicit reader intent

`TranscriptPanel` now recognizes Left/Right as well as the existing navigation keys, Ctrl+A, and explicit WPF editor selection commands. The command allowlist includes character/word, line/paragraph/page, document/line boundary selection and `ApplicationCommands.SelectAll`, including menu/assistive invocations. Handled routed commands and thumb drag events are observed too. Ordinary document mutation or `ScrollChanged` alone still cannot establish user intent.

The near-bottom reader-position check now receives whether a genuine selection gesture has a nonempty selection. Selection does not immediately rearm live follow just because its viewport is near the bottom. A later non-selection reader navigation, such as returning with the scrollbar, retains the approved resume-follow behavior. Programmatic caption changes do not reset that intent. No key bindings, command handling or normal text-selection behavior are replaced; the handlers observe routing and leave execution to WPF.

The WPF v10.0.15 [ScrollBar implementation](https://github.com/dotnet/wpf/blob/v10.0.15/src/Microsoft.DotNet.Wpf/src/PresentationFramework/System/Windows/Controls/Primitives/ScrollBar.cs) confirms that embedded scrollbar buttons route commands to their ScrollViewer and need not raise the standalone Scroll event. Explicit editor/vertical-scroll commands are therefore observed at PreviewExecuted as well as wheel/key/mouse/thumb input. The renderer's ordinary `ScrollToVerticalOffset` method calls are not treated as user commands. No global input hook or blanket scroll heuristic is added.

## Equivalent source-name accounting

All three implementations use a display/source name of at most 200 UTF-16 code units, stopping before a surrogate pair that would straddle that boundary. There is no Unicode case/NFC transformation. Backend wire events and retained records normalize the name, browser live/snapshot state applies the same shared helper, and native source parsing and byte accounting use the equivalent helper. Source IDs, generation and identity fields are unchanged; no new identity truncation or normalization is introduced.

The per-source 200-segment, 65,536-character and conservative 128 KiB budgets, 4,000-character caption bound, DRAFT lifecycle and stale-result guards are unchanged. Long-name tests deliberately reach the byte budget before 200 entries. They compare raw-input policy, live server state, browser state, actual native WebSocket state and reconnect retention, including a surrogate pair at the name boundary and preserved identities.

## Windows geometry/input coverage

The existing smoke harness now includes:

- WPF routed selection commands corresponding to Shift+Left/Right, Ctrl+Shift+Left/Right and Ctrl+A/menu Select all, followed by **same-source** partial revisions while selection is active.
- Routed preview/bubbling PageUp and mouse-wheel events through the real editor/ScrollViewer route; real scrollbar page/bottom commands and routed thumb drag start/delta/completion.
- An actual pending restoration, followed synchronously by selection input, another same-source revision and resize, before dispatcher callbacks are allowed to settle.
- Large multi-line partial-to-final shrink, repeated small/large resize and font cycles, loss of the current reading anchor through bounded pruning, and Combined/Detached transitions with restoration explicitly queued.

New helpers wait for measured/arranged controls, reader/restoration state and expected geometry across dispatcher-idle barriers, with bounded condition polling. The previous reading-geometry tests also use those barriers instead of most fixed post-update sleeps. Existing assertions are retained. Synthetic routed events/commands exercise WPF routing and default editor/control handlers; they are not a claim of physical keyboard/mouse or mixed-DPI hardware testing. Diagnostics remain bounded geometry/counts with synthetic content only.

## Existing Clear History semantics, unchanged

Clear removes existing server/client displayed history and current partials and broadcasts that change. It does **not** stop capture, clear scheduler queues, abort an active inference request or advance source generations. A completion that is still fresh and valid can therefore be published after Clear and become part of the new reconnect history, including text recognized from audio already in progress when Clear was clicked. The usual freshness/revision/generation guards still apply. A timer retirement cannot reconstruct an already-cleared draft because it carries no transcript copy.

New deterministic tests hold real scheduler promises across Clear, assert that the request's AbortSignal is not canceled, and verify the subsequent fresh partial/final result. This documents existing behavior rather than changing it into cancellation.

## Local validation and remaining limits

Production changes are limited to `TranscriptPanel.cs`, core `Presentation.cs` / `HistoryPolicy.cs` / `CaptionState.cs`, and `windows-captions/history-policy.js` / `server.js` / `overlay.html`. Tests change in the existing native state/session suite, WPF design smoke harness, Node caption-reading suite, browser suite and Node/native session driver. Documentation changes in this report, the original caption-reading report, Windows instructions and portable README.

| Local check | Result |
| --- | --- |
| Full Node regression suite | 101 passed, no failures/skips |
| Native core/state/settings suite | 183 assertions passed |
| Actual authenticated Node/native WebSocket suite | 19 assertions passed, including long-name byte-boundary live/reconnect equality |
| Chromium browser integration | Passed, including independent retention and the long-Unicode-name live/reload comparison |
| Launcher integrity/platform/diagnostics | 19 assertions passed |
| Managed audio conversion/discovery/identity/cleanup/buffer suite | Passed |
| WPF development and distribution cross-builds | Passed, zero warnings/errors |
| Portable acceptance harness and launcher cross-builds | Passed, zero warnings/errors |
| Both workflows through actionlint and git whitespace checks | Passed; workflow files unchanged |
| Existing verified Whisper runtime identity regression | Passed; native inputs/key unchanged |

One development browser run reached the existing five-second assertion deadline at 195/200 rows. The identical assertions passed on an isolated rerun and final sequential run; no assertion or timeout was relaxed. The cause of that transient timeout was not established.

Windows-only input/viewport assertions are cross-built, **not declared passed on Windows**. A direct local smoke launch stopped before application code with exit 150 because `Microsoft.WindowsDesktop.App` is unavailable on Linux. The new routes still need real Windows execution, especially default editor command execution, scrollbar command/drag handling, dispatcher races, selection after same-source revisions and reparenting/reflow geometry. Physical keyboard/mouse, mixed-DPI/multi-monitor behavior and long-session performance also remain unverified. Windows CI, portable runtime acceptance and physical acceptance are deliberately deferred until explicit authorization.

No Whisper runtime/cache identity, CPU/CUDA configuration, capture architecture, source scheduling, protocol authentication, preference format, Controls/tray/lifecycle behavior, visual theme or original Discord files are changed. No native Whisper/CUDA compilation is needed.
