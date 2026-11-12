# Rayme Phase 3 — modern UI refinement

Phase 4 supersedes the historical distribution and exit/browser-fallback behavior below. See [PHASE4_REPORT.md](PHASE4_REPORT.md) for Rayme.exe, official prereleases, explicit Quit Rayme and Auto/CUDA/CPU processing.

Starting commit: `c1f6366ba4057451f9ff5587f58f097847b1d9e0`, fetched and fast-forward checked on `feature/windows-loopback-captions`. This continues draft PR #1; no merge is performed.

The user reported successful physical Windows testing of the previous native overlay with live synthetic captions, including dragging, resizing, translucency and caption presentation. That is baseline functionality evidence. It is **not** a visual or physical test of this redesign.

## Design and implementation

All three surfaces now share a small, compiled WPF resource dictionary, `Themes/Rayme.xaml`. No styling package, browser engine or runtime dependency was added. The design uses dark blue-gray surfaces, restrained sky-blue accents, 8/12/16-DIP corner radii, consistent spacing, Segoe UI Variable with Segoe UI fallback, and Segoe Fluent Icons with MDL2 fallback. Solid surfaces and instant state changes keep rendering predictable with the existing software renderer; there is no blur, animated transition or GPU requirement.

The component system includes custom templates for rounded buttons and compact icon buttons, on/off switches, source dropdowns and dropdown items, sliders, selected color swatches, thin scrollbars and tooltips. Hover, pressed, disabled and keyboard-focus treatments are explicit. Controls have automation names, switches retain native checkbox/keyboard semantics, dropdowns retain native ComboBox navigation and text search, and sliders retain range/keyboard semantics. Source labels remain independent of accent color. Existing opaque text and the tested 70% minimum background alpha remain intact.

### Floating overlay

- Small settings, lock/unlock and hide icon buttons replace the large header text buttons. Their accessible names and tooltips state their functions. An embedded Rayme speech-bubble/R mark and compact wordmark form the drag area.
- Caption rows have calmer spacing, a source accent rule, an ellipsized source label with full-name tooltip, natural wrapping and a themed history scrollbar. The history can receive keyboard focus with a visible outline.
- Partials and finals use identical normal font style, weight, size and line height. A small LIVE badge signals unfinished text. Hiding the badge reserves the same header space, avoiding a typography or header-height jump.
- Visual row identity excludes final/partial state while protocol deduplication identity stays unchanged. A final event updates the same WPF row instance. The combined caption list is ordered by capture time, so completion alone does not move an otherwise unchanged row. Real text changes can still wrap differently, and genuinely earlier/later interleaved captions retain capture ordering.
- Finalized captions remain visible until the existing 30-row history bound, source invalidation or explicit Clear History removes them. There is no automatic time-based dismissal. Rendering updates existing text/rows; it does not rebuild the window or transfer focus on caption updates.

### Complete Controls window

The native control surface now has its own rounded, resizable shell with a branded header, close icon, compact status notice and one scrollable content column. It has four sections:

1. **Audio sources:** Application A, Application B and whole-device fallback cards. Each combines a switch, explanatory text and a fully themed dropdown, including an empty-selection prompt. Process choices retain PID labels. The existing server remains the configuration authority.
2. **Caption appearance:** text-size and opacity sliders with live numeric values; source-specific color swatches with selection rings and named tooltips. Existing saved custom colors also appear as selectable swatches.
3. **Window behavior:** switches for topmost, position/size lock and click-through; explanatory recovery text; restore-to-primary action.
4. **Session controls:** pause/resume, shared Clear History and availability refresh. Buttons still send the existing commands. No audio, scheduling or source-selection validation was moved into the UI.

The window remains usable at its 400-DIP minimum width; content scrolls instead of squeezing controls into fixed-height rows. Source descriptions and help text wrap. Error/connection notices use both text and color. Esc closes Controls. Captions remain independent of this control surface.

### Tray

`Assets/Rayme.ico` is a real multi-resolution application resource (16–256 pixels), embedded both as the executable icon and a WPF resource. The overlay, Controls and notification area use the Rayme mark instead of a generic information icon. The optional Pillow developer script rebuilds the committed icon; it is not needed to build or run the application.

The tray registration now uses documented `Shell_NotifyIcon` APIs with version 4 notifications. This provides the actual icon rectangle through `Shell_NotifyIconGetRect`, including keyboard invocation, rather than assuming that the mouse pointer marks the icon. Explorer restart messages re-register it. Icon/resource handles and hooks are disposed with the overlay. Hidden-overlay recovery and the private parent-pipe lifecycle are preserved.

The custom WPF flyout shares the application theme and contains:

- Show overlay.
- Open Controls.
- Lock/unlock position.
- Disable click-through and show the overlay.
- Restore to primary monitor, unlocking and disabling click-through first.
- Exit overlay, retaining the backend/browser fallback behavior.

Flyout bounds use physical monitor work areas and per-monitor scale, supporting negative coordinates and top, bottom, left or right taskbar anchors. `MonitorFromRect` selects the icon's actual display before clamping into its work area, avoiding ambiguity where taskbars sit between adjacent monitors. If Shell cannot report the icon bounds, the current pointer is the fallback anchor and placement is still clamped. The flyout supports Tab, arrow wrapping, Home/End, Enter/Space button activation and Esc; deactivation dismisses it. Focus is returned to the notification area on dismissal. Double-click recovery is retained, including version-4 selection notifications.

If custom WPF flyout construction or positioning fails, all six commands remain available through a dark styled native WinForms menu. If notification-area registration fails, hiding and click-through are not enabled, leaving the overlay itself accessible. Click-through still starts disabled on every launch.

## Preservation and preference compatibility

Preference schema version 1 is unchanged. Existing colors, text size, alpha, topmost, lock, monitor identity and placement load through the same validation/persistence code. No migration resets them. An older custom color outside the six standard colors is shown as a custom saved accent. Appearance changes still save locally; transcripts, application selections and credentials remain excluded.

Unchanged: Whisper configuration/inference, WASAPI capture, application isolation, scheduling, endpoint selection, authenticated HTTP/WebSocket protocol, session URL handoff, backend ownership/cleanup and all original Discord files. The new presentation helper does not change protocol keys or server state.

## Tests and evidence

| Check | Actual result in this Linux session |
|---|---|
| Node tests | All 33 pass, including endpoint-selection, native-launcher, security, source and original Discord integrity regressions. |
| Native overlay core | All 50 assertions pass. Eleven new checks cover existing preference compatibility, stable partial/final visual identity, generation separation, unchanged protocol identity and clamped tray positioning across scaled/negative-coordinate monitors and taskbar edges. |
| Actual .NET WebSocket client → production Node server | Nine native assertions and the Node fixture pass: authenticated session, rejected token/origin, source controls, pause/resume, reconnect snapshot and cross-client clear. Caption input is synthetic; transport/server are real. |
| Existing native tests | Conversion, silence, process discovery/identity, activation cleanup and buffer-limit checks pass. No audio endpoint was used. |
| Existing browser tests | Playwright/Chromium test passes for two-source labels, partials/history, reconnect, controls, pause/resume and clear across clients. |
| WPF build/package | Windows x64 self-contained cross-publish succeeds, including the compiled theme and embedded multi-resolution icon. No WPF build warnings/errors. This is build evidence, not Windows execution. |
| Windows CI / packaged GUI and lifecycle | **Not verified here:** Actions API requests return `403 Forbidden`. The existing Windows job is retained and expanded as described below. |
| New visual quality / physical tray/DPI behavior | **Not verified in this Linux session.** Physical baseline success applies to the previous UI. The redesigned surfaces require Windows inspection. |

The required packaged Windows smoke test now instantiates and exercises the real WPF controls. It verifies switch/template/focus state, slider changes and opacity, swatch selection, source/session command mapping, actual partial-to-final row reuse and measured height, literal annotations/source names, persistent final rows, dropdown opening, compact scrolling, embedded icon resolutions, tray keyboard navigation, fallback recovery commands and clear acknowledgement. Only that explicit smoke mode records synthetic commands instead of sending them to a live capture session; the separate real native WebSocket test remains required.

The smoke test writes `checks.json` and actual WPF-rendered PNGs to `artifacts/overlay-ui`: overlay partial/final, audio controls, dropdown, appearance, session, compact controls and tray flyout. These are produced by Windows WPF rendering, not HTML approximations or design mockups. The workflow uploads `windows-caption-overlay-ui-diagnostics` even on a failed smoke test when diagnostics exist. A required smoke or lifecycle failure still fails the job and prevents the success-only executable artifact upload; diagnostics do not substitute for a passing test.

The packaged private handoff/EOF lifecycle test remains required. Existing real Whisper and Windows audio jobs, checksums, pinned assets and accuracy/isolation assertions are unchanged.

## Build, installation and review

Workflow: https://github.com/fluidhelix/Rayme-live-caption/actions/workflows/windows-captions.yml

Draft PR: https://github.com/fluidhelix/Rayme-live-caption/pull/1

Update the existing feature-branch checkout to the redesign commit. Download `windows-caption-overlay-win-x64` from a successful matching Windows run, compare `CaptionOverlay.exe` with its artifact's SHA256, and replace `native\bin\CaptionOverlay.exe`. Keep the existing helper, model and `.env`. Launch through `START WINDOWS CAPTIONS.bat`; do not start the executable directly because it requires the existing private session pipe. The zip includes this report. The UI diagnostics are a separate artifact.

Developer commands:

```powershell
npm test
dotnet run --project native/CaptionOverlay.Tests -c Release
node test/native-overlay-session.js
dotnet run --project native/LoopbackCapture.Tests -c Release
python test/overlay-browser.py
dotnet publish native/CaptionOverlay -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/windows-overlay
node test/native-overlay-lifecycle.js artifacts/windows-overlay/CaptionOverlay.exe # Windows only
$env:RAYME_UI_SMOKE_OUTPUT = 'artifacts/overlay-ui'
$p = Start-Process artifacts/windows-overlay/CaptionOverlay.exe -ArgumentList '--smoke-test' -PassThru -Wait
if ($p.ExitCode -ne 0) { throw 'WPF UI smoke test failed; inspect checks.json' }
```

On the physical Windows 11 machine, inspect all three redesigned surfaces at the actual monitor scaling factors. Review the generated screenshots as supporting evidence, not as a substitute for interaction testing. Check every dropdown/switch/slider and keyboard focus path, narrow layouts, long source names, partial/final stability, ongoing final visibility, notification-area overflow, Win+B/keyboard context invocation, double-click show, click-through recovery, Explorer restart, monitor unplug/replug, portrait/upper monitors, taskbar positioning and normal shutdown. Confirm the new UI with the existing production Whisper setup; this session did not re-run that real-model end-to-end scenario. No game, anti-cheat or exclusive-fullscreen compatibility claim is added.

## Changed files

Added shared `Themes/Rayme.xaml`, `Theme.cs`, `ModernWindow.cs`, separated Controls/smoke partial classes, `TrayIcon.cs`, `TrayFlyout.cs`, embedded `Assets/Rayme.ico` and its developer build script. Added `CaptionOverlay.Core/Presentation.cs` and expanded native core tests. Updated `OverlayWindow.cs`, application resource initialization/project packaging, Windows workflow and Phase 3 documentation. Backend/audio/Discord modules are unchanged.
