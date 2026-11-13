# Approved design reference

Approved by the user, with the additional requirement that all Rayme-owned Controls, setup, dialogs and tray surfaces use neutral charcoal; caption surfaces use black and the live 16–60 text-size slider is retained. The original proposal below records the approved intent; current implementation and validation are in `OVERLAY_UI_REPORT.md`. The standalone HTML preview files remain outside the production package.

# Rayme overlay proposal — design approval only

Inspected checkout: `main`, `d7365b23352264d51260951b34cdb21813ded10e` (rc.41). This directory is outside the application repository. No production file, dependency, runtime asset, Git branch, workflow or release was changed. No native build or CI was run.

The user's first mockup is the primary visual reference; the two other images are evidence of the reported movement. These are browser-rendered studies, not screenshots of an implemented WPF update. Preview text is synthetic. Windows font metrics and native behavior require later verification.

## Visual proposal

- Black paint color exactly `#000000`. Preserve the existing 70–100% background-opacity range and the user's saved opacity; only background alpha changes, text stays opaque. Screenshots use 100% to show the requested black surface.
- Preserve existing `ColorA`, `ColorB` and `ColorDevice`, including custom values. Factory defaults read from `Preferences.cs` are A `#80DFFF`, B `#FFD68A`, whole-device `#CBB9FF`. Discord=A and Chrome=B are examples, not a hardcoded color-to-application mapping.
- Only the permanent source title uses its accent. Title text is the actual application name, with no “Application A/B” prefix, PID or repeated segment labels. Neutral body `#F2F2F2`; neutral LIVE badge, separators, icons and scrollbar. No colored vertical bars.
- One compact 36-DIP top bar with a fixed height. Stable A-then-B status order, actual names, single-line ellipsis, full text in tooltip/accessibility information. Failures, delay, pause and recovery remain visible; long explanations go in existing Controls without expanding the overlay's status row.
- Compact Hide, Detach/Reattach, Lock and Settings controls at the upper right. Detach/Reattach sits immediately left of Lock; Settings stays at the far right. The layout action includes a small text label for discoverability. Closed padlock = locked, open padlock = unlocked. Keyboard focus remains visible.
- Permanent 34-DIP source heading row, with a fixed LIVE slot. Hiding LIVE preserves its width/height. Body text is normal Segoe UI/Segoe UI Variable, naturally wrapped, with the existing independent text-size preference. Previews use 22 px as a sample, not a proposed forced reset of the existing 26-DIP default or saved size.
- Thin neutral horizontal divider only when both sources share a window. The transcript owns a permanent 9–12-DIP scrollbar gutter, even when it currently fits. Scrollbar appearance cannot change text width. Windows remain frameless, rounded and freely resizable; no fullscreen behavior or new minimum-size mandate.

## Layout contract

| Enabled sources | Default presentation | Detach |
| --- | --- | --- |
| A only | One window, one full-size panel | Disabled |
| B only | One window, one full-size panel | Disabled |
| A+B | One existing window, A above B, 50/50 split | Enabled |
| Whole-device fallback | One window, one full-size panel | Disabled |
| None | Existing window with a general source/setup prompt; no disabled-source caption panels | Disabled |

Enabling B while A is enabled changes rows inside the existing window. Disabling a source expands the remaining source. Ordinary speech/status/pause events never change row count. A temporarily unavailable but still enabled source retains its panel and meaningful health status; silence is not a reason to hide it.

**Detached mode is explicit and session-only.** It is available only with both application sources enabled. Both initially use Combined, including after relaunch and after re-enabling a second source. Only clicking Detach creates two native presentation windows. No additional mixed mode is proposed.

- Each detached window has one source, its own size, physical-pixel placement, monitor ID and lock. First-time positions are seeded near the current combined window on a visible monitor; saved positions may be restored with the existing negative-coordinate/DPI/off-screen recovery logic. The screenshot shows an example after the user positions them differently.
- Reattach is available in both top bars and in the tray menu. It restores the combined window's pre-detach placement and lock, with A upper/B lower. Independent detached placements/locks are retained for the next explicit Detach.
- Disabling either source while detached automatically reattaches the remaining source into one full panel. Re-enabling the other source remains Combined until another explicit Detach click.
- Per-source scroll anchors survive detach/reattach, not just raw offsets: text is still readable at a different width. Transcript history remains the same shared in-memory backend/state history, with its existing bounds and source identity rules. No transcript or scroll text is written to settings.
- Font size, background opacity and source colors remain shared appearance preferences. Independent lock/placement does not introduce a second settings or capture authority. Existing settings load without resetting any colors, opacity, text size or combined placement; new placement/lock fields should be additive and validated.

### Window and process lifetime

This is a view change in the existing overlay process. One authenticated `SessionClient`, one authoritative `CaptionState`, one backend, the existing capture workers and one shared Whisper scheduler remain. Detach/Reattach sends no capture/model restart command and launches no second overlay executable.

A small presentation coordinator would own the Combined/Detached state. The combined session host can remain hidden while the two peer windows are shown. Changing layout must not invoke the current main-window Closing→Quit path. Test WPF window ownership carefully: hiding the session host must not hide the detached windows or make Controls inaccessible.

Hide while detached hides the caption group to the existing tray; Show restores the selected presentation. Quit Rayme/normal window quit remains a whole-session action with the existing secure private intent. Closing Controls closes only Controls. Reattach and source-driven consolidation are explicit view transitions, never user quit. Backend EOF/failure must close every owned presentation window. Keep one tray recovery surface; recovering/unlocking/disabling click-through must remain available for all visible windows.

## Custom color picker

Location: **Controls → Captions → Source colors**. A row shows the actual assigned application name, current swatch and current hexadecimal value; whole-device has its own row. Assignment controls may still identify slots A/B, but caption titles never do.

The proposed compact native picker has:

1. Saturation/brightness field and hue slider.
2. Editable `#RRGGBB` and Red/Green/Blue fields, with keyboard access and input validation.
3. A black sample showing the selected source title and neutral body text.
4. Restore default, Cancel and Apply. Restore default selects that source's original default in the draft; Apply commits it. Cancel leaves the saved color unchanged.

Existing custom colors are kept exactly. Apply persists through the existing atomic preferences path. Changing one source does not recolor another or change caption body text. Low-contrast choices receive a readable warning; a color swatch is never the only way to understand the selected value. No model/audio behavior changes.

## Why the current captions can jump

The screenshots show different text at the top of the reading area after speech ends. They are consistent with scroll/measurement changes, but do not establish which invalidation fired. The following are confirmed in the inspected code:

| Code | Observed mechanism |
| --- | --- |
| `OverlayWindow.cs:234–242` | `changed` includes any change to final text, partial text or font size. Every such update can schedule `ScrollToEnd()` while Following is true. Final correction and actual new speech are not distinguished. |
| `OverlayWindow.cs:225–226` | Scrollbar visibility is Auto; appearance/disappearance can change wrapping width. Follow intent ignores events with extent or viewport changes. |
| `OverlayWindow.cs:26,61–73,150–158` | Status is a wrapping TextBlock in an Auto-height row, updated for every event. A longer status/error can change transcript viewport height. Dictionary enumeration also lacks an explicit source display order. |
| `Presentation.cs:35–42` / `OverlayWindow.cs:233–242` | Final history is rejoined into one run and the partial tail into another. Finalization may correct words, add punctuation, change wrapping or shrink extent. A saved raw pixel offset cannot identify which words the user was reading after those changes. Offset is captured after assigning text, rather than capturing a text anchor before mutation. |
| `OverlayWindow.cs:223,231` | LIVE already uses Visibility.Hidden, which preserves its layout slot. It is not a credible primary cause by itself. |

**Strongest suspect:** final recognition changes the measured text extent, then the same automatic bottom-follow code used for new text runs. When extent or viewport height shrinks, the ScrollViewer may also clamp its offset before/without that callback. The two supplied status lines appear to fit on one line, so status wrapping is an additional code risk, not a proven explanation for those exact screenshots. Accurate final correction itself must remain.

### Proposed correction after approval

1. First add a bounded test-only geometry trace using synthetic events: event kind, source/generation/segment/revision, offset, extent, viewport dimensions, actual wrapping width and status/title row heights. No raw audio or transcript logging. Reproduce on Windows before selecting the precise fix.
2. Fix status/title heights and scrollbar gutter. Status/LIVE changes update only those fixed areas; they never call a transcript scrolling method. Use deterministic source order and stable typography/line height.
3. Preserve segment identity as continuous native inline text. Update the existing segment run on partial→final, rather than rebuilding a joined prefix and deleting a partial tail. No cards/newline blocks are introduced. Keep the Phase 5A generation/revision/freshness filters authoritative.
4. Capture the first visible text anchor **before** mutation: source/generation/segment plus text position and pixel displacement. Restore after layout. Map corrected positions to the nearest surviving word/unchanged region; never keep an incorrect or retired partial merely to preserve pixels.
5. Separate true appended speech from final correction and health/status events. Only append/growth while already near bottom may advance the viewport to reveal new text. Final correction, silence and status changes restore the reading anchor instead of blindly scrolling to the end. Reader navigation cancels pending follow callbacks.
6. Test extent shrinkage, not just growth. If necessary, retain a bounded temporary end-of-content cushion so native offset clamping cannot move the viewport before anchor restoration. It contains no duplicated/obsolete text and is settled without a forced jump. If the anchor text itself is removed, choose the closest surviving correct anchor; do not promise every corrected glyph can stay at its old position.
7. Each source owns its selection and follow/anchor state. Preserve it when rows split/merge or views detach. Existing history trimming stays bounded and its anchor fallback must be tested.

The existing TextBlock does not provide normal native text selection. To support the requested selection/drag distinction, evaluate a lightweight read-only native selectable text surface with stable inlines/text positions (for example a borderless, single-paragraph RichTextBox). Keep its WPF theme, spacing, software rendering and bounded document cost; benchmark the chosen surface before committing to it. This does not change the recognition architecture.

## Dragging contract

Unlocked: click-drag a source title, the status area's empty space, blank caption space or background padding. Use the documented drag threshold and normal WPF drag behavior. Text glyphs/selection areas, links if ever present, buttons, inputs, scrollbar tracks/thumbs and resize grips are excluded. Dragging actual text selects text; it does not move the window. Locked: movement and resizing stop, Settings and text selection still work. A detached window's lock never changes its peer's lock.

## Preview files and checks

- `index.html`: self-contained visual board. `interactive.html` opens the layout-switching workbench directly. `?interactive=1` shows the layout-switching workbench; source checkboxes and Detach/Reattach are synthetic presentation controls only.
- `single.png`, `combined.png`, `detached.png`, `picker.png`: standalone screenshots.
- `overview.png`: the complete visual board.
- `verify_preview.py`, `preview-checks.json`: 32 passing lightweight browser assertions for the prototype only, including source-count layouts, explicit Detach, source-loss reattachment, independent locks, name-only labels, original/default/custom colors, persistence, geometry across speaking/silence, selection/drag behavior and background-only opacity.
- `capture.py`: captures the design screenshots using installed Chromium; no native build, backend, model or Internet is involved.

HTML checks do not validate WPF measurements, real detached HWNDs, monitor hot-plug, mixed DPI, native text selection, inference or the reported native scrolling failure. All of those remain implementation/Windows acceptance checks after design approval. No production fix is claimed at this stage.

## Approval requested

Approve or revise: black/name-only visual treatment; compact Detach/Reattach beside Lock and Settings; dynamic Combined as the default; explicit two-window mode only with both sources; independent per-window lock/placement; shared appearance settings; custom picker/Restore default behavior; drag-on-blank versus select-on-text interaction; anchor-based scrolling correction with accurate text retained.

Implementation, regression tests, cached native artifact reuse and Windows CI follow only after the user approves this proposal. No merge, push or release is part of this approval stage.
