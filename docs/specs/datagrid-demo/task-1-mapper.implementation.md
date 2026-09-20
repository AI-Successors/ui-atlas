# Task 1: mapper and diagnostics implementation

Status: approved live discovery and information-card increment implemented on 2026-09-19. Governing [requirements](task-1-mapper.requirements.md).

The historical information-card steps and verification below describe the original overlay increment. In the combined worktree, [Task 3's current explorer](task-3-extraction.implementation.md) supersedes that card with Explore / Stop and image preview/export. Retain the source-aware highlights, exterior movable legend, persistent recording overlays, passive refresh, and occlusion clipping. Image exploration is an explicit capture exception to normal persistence: hide decorations for the exploration interval, keep Stop available outside the table, and restore decorations afterward. Its image result does not qualify OCR or business-data completeness.

## Approved overlay implementation

1. Keep discovery in the existing recorder frame pipeline. Preserve source/identity information through overlay projection instead of reducing every observation to a green rectangle. Add a pure classification/projection policy with four semantic types. Native Grid/Table patterns outrank visual duplicates; visual synthetic patterns never prove native support. Bound and deduplicate whole-grid candidates, suppress contained cell/text overlays, and omit unclassified structural containers.
2. Feed the policy from initial scans, adaptive full-frame refreshes, and fresh identity observations when resuming a saved map. Include visible grid-like native child hosts where UIA does not describe the table. This fallback reports a native-host hint, not computer vision or verified native support. A hidden or zero-sized application owner must not replace the selected visible surface for host discovery. No schema, cell-value acquisition, scroll, or persistence changes are needed for this increment.
3. Extend the existing overlay using a separate partial implementation. Keep ordinary control outlines click-through. Give current grid regions non-activating hit windows so inspection clicks terminate in Mapper. Hide them and the inspector only during actual screenshot capture; automatic input makes them disabled/click-through while leaving the visuals present. Retain Recorder overlays in user screenshots. Never forward a grid inspection click to the application.
4. Add one small reusable WPF inspector with the Recorder's off-white shell, rounded corners, fine border, restrained shadow, and Inter/Segoe UI text. Reuse a single inspector for selection changes. Include drag, close and Escape, with accurate source/native-support text and no acquisition actions.
5. Track current target visibility, identity, position and surface applicability independently of foreground focus. Refresh invalidated detections, clip against covering windows, and close all auxiliary windows on disposal. Use screen-to-DIP conversion for drawing and enforce physical input-window bounds independently of the desktop overlay's DPI.
6. Add behavioral tests around evidence classification and overlay lifecycle, render both inspector states for visual review, and perform a real synthetic-window click-isolation smoke. Build and run relevant recorder regressions before the full suite. Record verification evidence below when complete.

The earlier mapper/storage/acquisition plan below remains the broader pipeline context. This increment does not change reading qualification or grant traversal approval.

## Legend and missing UIA outline correction

- Move the legend from the target's drawing canvas into `MapperLegendWindow`. Use real monitor work areas to find exterior space, including negative monitor coordinates. Keep its WPF size in DIPs while positioning in physical pixels, so crossing monitor scales does not inflate the restore button.
- Use a draggable header and a Hide button. The collapsed 34-DIP button distinguishes a click from a drag; a click restores the panel. Keep the same window and state through snapshot refreshes, capture hiding, and automatic-input transparency; clamp expansion at desktop edges and close it with the overlay.
- Root cause of the missing Abacre button outlines: the first presentation policy allowed standard interactive UIA roles but excluded generic `Pane` controls. The passive Abacre capture still contains the toolbar/action buttons as visible Win32 UIA panes. Preserve named UIA leaf controls (`Pane`, `Custom`, `Group`, `Text`) outside grids while excluding enclosing layout containers and cell fragments. The collector and saved observations are unchanged.
- Regression coverage includes legacy generic controls, exterior positioning on either side of the desktop origin, constrained/fullscreen space, hide/restore button events, DPI size, capture hiding, refresh state, and teardown. Desktop visual review confirms the legend is outside the synthetic target. The Computer Use helper rejected the exterior legend's screen coordinates after one refreshed retry, so physical legend drag/click verification remains a separate limitation; routed button-event checks run against the real WPF window.

## Verification of the approved increment

### Persistent overlays during recording

- Root causes: `RefreshMapperTarget` used foreground focus as a visibility gate; native-host/layer invalidation cleared the entire Mapper snapshot; the existing passive layer probe never rebuilt typed Mapper highlights. The next recorded frame could make them appear again, producing the reported click-dependent behavior.
- Keep the legend alive for the session and paint all available target regions regardless of foreground focus. Clip the drawing and native grid hit regions against windows above the selected surface; unrelated apps still receive their own clicks.
- Reuse the existing background UIA refresh to rebuild current source-aware highlights and native host candidates. Continue probing after an empty/invalidated snapshot. Replace changed layers from fresh evidence, retire invalid grid hosts individually, preserve still-applicable visual candidates, and reject background results overtaken by a newer frame or target movement.
- Do not treat an unavailable/screenshot-only observation as an empty scan. Automatic-input scopes make owned windows input-transparent without hiding their visuals. Nested screenshot scopes hide briefly and restore when the final hold ends.
- The real-window regression includes a separate synthetic process taking foreground and partially covering the target, input hit testing over that process, passive control changes, Orders/Stays tab switches without recorded clicks, resize recovery, and repeated nested capture/restore scopes. Live Abacre interaction is not claimed by the synthetic regression.

- The scoped implementation is present in the task worktree and the main checkout used by Visual Studio. Existing unrelated changes and the user's screenshot-visible Recorder behavior are preserved.
- After the persistence correction, Debug and Release solution builds in the main checkout pass with zero warnings/errors. The full main-checkout Release suite passes: 350 core tests and 352 Windows tests, 702 total, including the expanded lifecycle regression above at two desktop positions. Repository-boundary and whitespace checks pass. Relaunch Mapper to load the rebuilt version.
- Classification tests cover real UIA pattern names, synthetic visual Grid patterns, native host hints, duplicate suppression, multiple grids, cell suppression, hidden/outside controls, empty scans, and the hidden zero-sized application-owner regression.
- A real WPF target with synthetic data exercises native and visual candidate hit regions, card selection, capture hiding/input transparency, movement at two desktop positions, minimization/resize recovery, empty refresh, and teardown. Physical hit-window bounds are compared with observed grid bounds before and after movement.
- Computer Use physically clicked both grid types, closed and dragged the card, and clicked an ordinary button. The receipt records one ordinary application/button click, zero selected business rows, and both `NativeGrid` and `GridCandidate` inspections. Both card states were rendered and visually reviewed.
- A passive recording of the running Abacre Orders window found one whole-grid candidate via `TAbacreDBGrid`, with one visible inspection hit window. The source remains `Native window hint`; UIA-native support was not proven. The recording saved with zero interactions. No traversal or business-data mutation was performed.
- The Computer Use helper did not expose the Abacre target on its monitor, so physical clicking of the actual Abacre candidate remains unverified. This is separate from the passing synthetic physical-click check and the live Abacre discovery evidence. No claim of grid reading, schema qualification, or complete acquisition is made.

Local evidence remains in ignored `artifacts/mapper-overlay-qa/` directories and Mapper diagnostic logs. Customer screenshots/recording data are not source assets. The broader durable mapping and acquisition plan below is not marked complete by this overlay increment.

## Smallest change

Add one table-oriented path in the existing mapper and one compact diagnostic dialog. Bypass the current table-to-cell-control projection for the selected demo grid; retain unrelated mapping behavior. Reuse window capture and target metadata, not the existing generic cell detector as the new table definition.

Useful public seams: [QuickSurfaceScanner](../../../src/UiAtlas.Core.Cli/QuickSurfaceScanner.cs), [recorder orchestration](../../../src/UiAtlas.Core.Cli/Program.cs), [MapCurationStore](../../../src/UiAtlas.Core.Storage/MapCurationStore.cs), [LocalArtifactCatalog](../../../src/UiAtlas.Core.Storage/LocalArtifactCatalog.cs), and [UiMappingReadModel](../../../src/UiAtlas.Core.Reader/UiMappingReadModel.cs).

Prefer an optional mapped-grid collection in the existing map curation document. Keep the logical map and host stable keys as authority; missing fields in old documents mean no mapped grids. Use existing atomic persistence and validation. Follow existing local copy/delete behavior; do not introduce a second catalog or a SQLite migration. General export/import support is deferred; privacy-safe exports must not start exposing grid labels or evidence.

## Shared handoff agreed in the first 20 minutes

Use a few plain records in the existing Contracts project. These are field meanings, not a new protocol framework. Task 1 owns mapping records, Task 2 capture/progress records, and Task 3 extracted-data records. Agree the names once, then keep separate files to avoid conflicts.

| Record | Minimum content |
| --- | --- |
| MappedGridDefinition | Grid ID, logical map ID, owning surface/host stable keys, parent-scoped locator, relative data/header/body regions, enclosing scroll scope, axes, schema, qualification and reason. |
| GridSchema / Column | Schema revision, completeness, ordered column keys/labels, physical intervals, header/body and row geometry. Column identity is separate from label. Bounds are hints to revalidate. |
| AcquisitionRequest | Definition, mapping-survey or fresh-read mode, limits, cancellation, and explicit operation approval. No arbitrary click/type instructions. |
| AcquisitionProgress | Acquisition ID, lifecycle, stage, elapsed time, tile/row counts, bounded diagnostic reason. |
| CapturedGrid | Schema revision, source/process instance and capture interval, original tile references, accepted placement/row regions, boundary/continuity evidence, capture status, restoration outcome. |
| GridReadResult | Acquisition ID, mapped source, schema, rows and source references, data status/reasons, capture status, extraction status, restoration, timings. |

Lifecycle is awaiting-approval, running, or finished. Stage is bind, probe, schema, capture, join, restore, extract, or verify. Terminal data status is complete, partial, or failed. Cancellation/decline is a reason, not an extra completeness value. A result with no trustworthy data after an error is failed; a verified subset is partial. A known empty table can be complete only with structural and boundary proof.

## Implementation steps

1. Surface one candidate from current host evidence or an explicit user-selected host/rectangle. Task 3 supplies viewport structure and capability assessment. Do not generate one map node per business cell.
2. Display the initial column fragment and reading status. Obtain approval before Task 2 surveys offscreen columns/rows. A mapping survey may extend a provisional schema; a later fresh read validates the saved schema and never silently edits it.
3. Save a complete reviewed schema only after width/structure verification. Preserve a candidate/incomplete definition when qualification fails, with a reason. Reopening must restore that distinction.
4. Wire one Read/Survey button and Cancel to the shared service from Tasks 2/3. Reuse the same small confirmation UI for MCP: identify application/table, axes, limits, and that the viewport will move. No generic consent broker.
5. Show progress and final table preview using existing controls. Offer access to local evidence/error details; skip new dashboard navigation or editing features.

## Dependencies and ownership

Task 2 owns target binding, all input, the common operation gate, traversal, and restoration. Task 3 owns schema interpretation and cell reading. Task 1 owns durable storage and UI only; do not duplicate either engine in the CLI. Stop/pause recorder activity at a safe boundary before surveying; do not redesign continuous recording.

Focused checks: old curation loads, definitions round-trip, duplicate labels retain separate keys, incomplete results preserve the prior schema, and one existing recorder/map smoke remains valid. Target: provisional contracts/UI by 0:30, storage and service wiring by 2:00, live mapping integration by 3:00.
