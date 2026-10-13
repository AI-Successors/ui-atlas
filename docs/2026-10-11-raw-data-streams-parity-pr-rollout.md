# Raw Data Streams capture and editor parity rollout

Latest follow-up: [retained popup containment failure and recovery](2026-10-13-retained-popup-containment-rca.md).
Previous follow-up: [popup evidence, incomplete captures and selection repairs](2026-10-12-raw-popup-evidence-repairs.md).
Previous qualification: [MSAA dropdown coverage and fresh Word quality qualification](2026-10-12-word-msaa-coverage-and-quality.md).
This corrects the overly broad interrupted-frame exclusion in the
[foreground eligibility follow-up](2026-10-12-raw-observation-eligibility.md):
foreground provider evidence stays visible with timing warnings, while unsafe
overlays remain withheld and background windows remain excluded.

Status: corrective implementation complete and qualified below (2026-10-11). The earlier qualification
proved record retention and crop alignment, not isolated window pixels, reliable
native ownership, or coherent overlays. The live-word review exposed those gaps.
Draft PR #1 remains unmerged; qualification must include the corrective gates below.

## Corrective rollout: live-word review

### Goals and evidence

- Frame 3 captured pixels before the Font tooltip appeared, then retained later
  UIA records. Keep interrupted attempts inspectable without presenting them as
  coherent observations. Before/after checks qualify stability, not atomicity.
- Main Window frames 6–11 and Font frame 11 contain covering dialogs because
  screen pixels were flattened before cropping. Retain independent HWND images
  with their own bounds, timestamps, method and coverage. A screen fallback is
  explicitly occluded/unknown evidence, never an isolated window image.
- Tooltip Control-view results include Word controls. Separate query HWND from
  reported native HWND and verified ancestry; stop traversal escaping its root.
  Legacy ownership remains unverified rather than retrospectively invented.
- The twelfth card is resumed-session frame 1. Its screenshot exists but the
  explicit first bundle replaced the auto-resolved evidence set. Resolve all map
  sessions, union matching attachments, and expose missing-bundle diagnostics.
- OCR/geometry are provider evidence, not native window families. Keep every
  record accessible through frame stream inspection without counting image-source
  groups as native surfaces or source records as unique physical controls.

### Non-goals

No raw scene reconstruction, hidden-pixel synthesis, temporal backfill, forced
input blocking, automatic dialog traversal, changes to test5, remote model
activation, or changes to grid acquisition/export. Preserve original recordings
and unrelated working-tree changes. Existing composites cannot be repaired into
isolated surfaces; legacy display must disclose that limitation.

### Ordered PR units and to do

1. **Evidence attachment and presentation**
   - [x] Resolve every source session when opening a merged map, including when
     an explicit evidence path is supplied; never silently replace other sessions.
   - [x] Distinguish missing attachment, missing image, and invalid evidence.
   - [x] Label session/frame identity and source-entry counts clearly.
2. **Per-window pixel contract and capture**
   - [x] Add backward-compatible per-HWND image references, bounds, acquisition
     intervals, capture method and coverage; validate entries and budgets.
   - [x] Retain independent WGC images before any composition. Preserve original
     screen frame as supporting evidence and input to image providers/stability.
   - [x] Render only a selected window's independent pixels in native raw views;
     explicitly label/mask occluded screen fallback instead of inventing pixels.
3. **Provider scope and temporal qualification**
   - [x] Preserve query target separately from actual reported native handle;
     verify raw ancestry for filtered traversals and reject scope escapes.
   - [x] Keep incomplete/changed attempts accessible but visually separate;
     suppress misleading synchronized overlays for changed/unverified attempts.
   - [x] Never choose a provider default solely because an invalid scope returned
     more records. Keep timestamps and scope visible in source inspection.
4. **Raw provider navigation**
   - [x] Remove Image streams as a peer native-window family. Offer an explicit
     frame-stream inspector for OCR/geometry, empty outcomes and all raw records.
5. **Regression and live qualification**
   - [x] Test merged-session attachment, per-window image save/reopen/validation,
     occlusion fallback, interrupted overlays, provider scope and source navigation.
   - [x] Reopen retained live-word with both bundles and inspect resumed frame 1.
   - [x] Fresh Word capture: root + Font + Colors; color dropdown; interrupted
     capture. Verify window-specific pixels, provider ownership and honest status.
   - [x] Run focused storage/raw/grid checks and broader suites; compare unexplained
     failures against baseline. Build user-facing outputs through normal VS.
   - [x] Record exact results and limits here and update the existing draft PR.

### Acceptance and rollback

Font and Colors in one observation remain two window instances, not invented
time frames. Each displayed native image has a traceable HWND image reference;
fallback/legacy occlusion is explicit. Changed observations cannot look like
successful synchronized overlays. Both sessions in live-word show their actual
screenshots. Image-provider records remain inspectable but are not window nodes.
No grid contract or source record is silently discarded. Revert scoped commits
for rollback; never mutate retained source bundles or bypass integrity checks.

### Corrective execution evidence (2026-10-11)

Implementation commit: `6d5ac0ce87f3fb226cf59f189979c75440aca0ad`.
Provider-selection follow-up: `b174fea`.

- Additive window-image references retain HWND, exact pixel bounds, start/end time,
  method and outcome. WGC images are bounded to 16 MiB each, 32 MiB per capture,
  two seconds per window and five seconds cumulatively. Validation rejects foreign
  frame/window references, missing images, duplicate planes, invalid times and
  PNG dimensions inconsistent with the recorded rectangle. DWM extended bounds
  account for invisible resize borders without stretching the pixels.
- UIA records distinguish the query HWND, reported native handle and bounded
  Raw-view ancestry verification. Traversal stops when ancestry escapes the target;
  rejected evidence stays in the stream inspector with unresolved ownership.
  Legacy query targets remain explicitly unverified. The original composite and
  all OCR/geometry data remain available through Frame streams.
- Reopened retained `live-word.db` with only the first bundle explicitly supplied.
  Automatic attachment also resolved `live-word-resume02`; its frame 1 screenshot
  displays correctly. All 12,492 source entries remain available, with 21 native
  window observations and three native families. Image streams is no longer a
  peer window family. A legacy crop displays explicit known-occluder masking.
- Fresh normal-output Word recording: `word-isolated`, four observations, nine
  independent HWND images, four original frame images, no missing screenshots,
  and two complete interactions. Main Window remains free of covering dialogs;
  Font remains free of Colors; Colors has its own image. These three saved images
  were visually inspected, including the actual invisible-border coordinate offset.
- Initial observation is `ok/stable`. Font is `partial/stable` because modal UIA
  providers time out. Opening Colors during the color-palette acquisition retains
  `interrupted/changed`, then creates a separate `partial/stable` three-window
  observation. The palette's Raw UIA attempt returned 80 verified records; later
  palette reads failed after it closed and remain failed attempts. No tab traversal
  or document formatting change was performed.
- Finish Map saved successfully: 5,685 nodes and 7,031 edges. Bundle validation,
  stored graph validation, clean-source rebuilding and subsequent validation pass.
  A field-equivalent comparison after save/reopen confirms all **4,459 raw records**,
  zero missing/extra records, and no overlapping acquisition intervals. Recording
  SHA-256: `7388316b11956fea9f9042373bcb1e314bc2a1b1ed20deee5e3a556d34c538bc`.
- Clean task snapshot (excluding unrelated working-tree edits): full Release
  solution builds with zero warnings/errors. Only the disposable snapshot's SDK
  feature-band selection was adjusted. **532/532 Windows tests pass**, including
  a real overlapping blue-parent/red-popup WGC test and UIA scope checks in all
  three views. **461/461 Core tests excluding the seven native UI cases pass**.
  Focused raw/evidence/grid/boundary checks pass **50/50**; final boundary and raw
  follow-up after the screenshot-request guard passes **30/30**.
- Clean native Core follow-up: **3/7 pass**. The same four earlier baseline failures
  reproduce: mapper inspector placement and three opaque-grid traversal cases.
  Combined clean Core result is **464/468**. These remain explicit release gates.
- Normal Visual Studio Build produced **UI Atlas Core 2.0.71**: 12 projects built,
  two up-to-date and zero failures. The executable product version points to
  implementation commit `6d5ac0c`. The fresh recording used build 69; build 71 also
  includes stricter PNG validation and the final inspector diagnostics. Normal
  output includes the user's pre-existing edits, excluded from the clean snapshot.
- The working-tree full Windows run before the final test addition passed 536/536;
  it includes user-owned tests and is separate from the clean count. Its Core run
  reproduced the four previously baseline-confirmed native failures plus two
  temporary repository-root lookup failures. With an explicit root, the remaining
  working-tree boundary failure is an absolute path in an unrelated untracked
  document; the clean snapshot's boundary checks pass. That document was untouched.
- Final viewer inspection exposed a same-frame selection leak: switching Font to
  Colors kept Font's MSAA attempt selected, making Colors appear to have zero
  records. The provider default now resets for each window observation while
  retaining an explicitly selected empty attempt when staying within that scope.
  The follow-up clean Desktop Release build passes with zero warnings/errors.
  Normal Visual Studio Build then produced **UI Atlas Core 2.0.72**, seven projects
  built, seven up-to-date and zero failures, with product version `b174fea`.
  Live Font/frame 4 to Colors/frame 4 selection now changes from Font's 236 MSAA
  entries to Colors' 18 UIA Raw entries, with all 140 Colors entries retained.
  Font/frame 4 and Colors/frame 4 display their separate independent images.

Limits: independent pixels require a fresh recording; old flattened images cannot
recover covered content. Masking only covers known recorded occluders. WGC failure
uses a labelled screen fallback. Stability checks do not make Windows APIs atomic.
Word's modal UIA timeouts remain real partial outcomes. The exact old Font-tooltip
Control-view anomaly was not replayed to successful completion; ownership checks
are qualified by the fresh palette and overlapping-window test. Grid contracts
pass; this run is not a new customer-grid export certification. Private evidence
and test logs remain local; unrelated changes and test5 are untouched.

This is the execution plan for the 07:43 video review. It amends the existing
draft PR #1, with five ordered, separately reviewable implementation stages.
Earlier raw-frame isolation and geometry repairs remain prerequisites, not proof
that full/manual capture or window presentation is correct.

## Problem and evidence

The retained Word recording has 53 records: 32 observations and 21 supplementary
records without screenshots. Of 46 additional UIA Raw reads, 36 timed out. Frame
8's pixels were captured before frame 7's provider work finished. Frame 3 lists
both OpusApp and the Font dialog but contains only dialog pixels. Window mode
displays those pixels under Main Window; frame 21 displays all of Word when the
Colors dialog is selected. All 4,526 HWND-0 records are OCR, but the projection
turns them into an invented native-window family. Hierarchy selection loses the
family scope that topology selection retains.

Reference: test5 HEAD 958df4414b4daf5317be6362ce93d881987e53ea and UI Atlas HEAD
273235d33ffa6d48877522890bfe24ee04d57432, plus the existing working-tree changes.
The supplied recordings are from different sessions; their counts are not a
controlled accuracy or performance comparison.

## Goals

- A manual observation collects its in-scope native windows, pixels and available
  passive provider streams, then seals its evidence before reporting readiness.
- Each provider response has source/attempt identity, acquisition times, status,
  evidence kind and scope. Empty success is distinguishable from missing data.
- Capture changes/interruption are explicit. No screenshot or control tree from
  another observation can masquerade as current evidence.
- Native surfaces come from recorded Win32 windows. Families organize them by
  process/class/native characteristics; a variant identifies one observation and
  one window instance. Repeated-looking observations remain available.
- Preserve provider ownership, geometry and duplicate attempts. Image-derived
  evidence remains inspectable without inventing native window ownership.
- Every AppMap projection uses the selected window's recorded image coverage and
  coordinate transform. Hierarchy and topology select the same family/variants.
- Preserve grids, privacy, storage integrity, existing recordings and Raw World.

## Non-goals

- No scene reconstruction, temporal backfill, control reparenting, or geometry
  correction in Raw Data Streams. Derived interpretations belong downstream.
- No promise that Windows APIs execute atomically or that the target process is
  physically frozen. Collectors must detect changes and disclose uncertainty.
- No automatic dialog-tab traversal or grid scrolling in attended capture.
- No activation of remote vision models or new credentials/billable providers.
  Unconfigured sources remain explicitly unconfigured.
- No replacement of grid acquisition/export with generic raw observation.
- No port of test5's compatible-rich-observation backfill or silent geometry
  enrichment. Native category labels are derived from Win32 attributes/class
  heuristics; they are not literal Win32 enum values.
- No rewriting the user's source recording, unrelated changes, or test5 code.

## Contract and invariants

The lifecycle is settle -> begin observation -> capture native scope/pixels ->
collect configured streams -> check stability -> seal -> ready. Provider reads
are bounded, cancellable and serialized when required. Internal targeting probes
are supporting attempts, not substitutes for a user-visible full observation.
Interruptions produce retained incomplete evidence; recovery is bounded.

Store session/frame/action identity, acquisition interval, capture method,
coordinate mapping and coverage. Preserve the original reported owner separately
from screenshot association. A native window listed in a frame does not imply
that its pixels or controls were acquired. Missing coverage must remain missing.
Source counts mean retained records, not deduplicated physical widgets.

Legacy bundles remain readable. Supplementary legacy records are inspectable as
such, with no invented parent observation or screenshot. New metadata is additive
and versioned. All raw records continue through existing privacy/size limits.

## Stage 1 / PR unit: contracts and evidence fixtures

### To do

- [x] T01: Introduce observation acquisition metadata and typed stream provenance;
  distinguish native automation, image-derived records, pixels and window inventory.
- [x] T02: Add small synthetic/redacted fixtures for dialog-only pixels, popup
  evidence, overlapping intervals, duplicate attempts, HWND-0 OCR and invalid bounds.
- [x] Preserve bundle backward compatibility and semantic hash round trips.

### Acceptance

One sealed observation has its own coverage and terminal stream outcomes. Raw
entries remain byte-equivalent in relevant provider fields after save/reopen.
Old metadata absence is represented as unknown, not guessed complete. Tests reject
cross-frame substitution and wrong-window image association.

## Stage 2 / PR unit: coordinated full observations

### To do

- [x] T03: Route initial/manual/root/dialog/popup observations through a common full
  capture boundary; retain specialized probes only as explicit supporting work.
- [x] T04: Replace the four-second/700-ms rich sweep with a bounded full profile,
  per-provider deadlines and progress. Avoid starving owned windows behind the root.
- [x] T05: Check input/window topology/bounds/DPI/state changes across acquisition;
  retain interrupted attempts and recover with a bounded fresh observation.
- [x] Keep ordinary same-looking observations; do not use visual deduplication to
  erase accepted manual observations. Publish readiness only after sealing.

### Acceptance

The frame-7/8 overlap cannot be accepted as synchronized. Opening a dialog or popup
does not replace the root inventory with a point probe. Slow and cancelled reads
have honest statuses; late responses cannot amend sealed evidence. Human input is
not blocked, and manual capture does not trigger active tab/grid exploration.

## Stage 3 / PR unit: native-window and stream projection

### To do

- [x] T06: Stop turning image-source HWND 0 into a native surface. Preserve OCR and
  geometry in typed source evidence and associate pixels only through provenance.
- [x] T07: Keep window-family and frame/window-instance identity separate; propagate
  exact pixel coverage, class/style/ex-style, source counts and capture health.
- [x] Keep real unknown native ownership in diagnostics, without guessing a popup.

### Acceptance

No synthetic native-window family is created for this recording's 4,526 OCR
records, and none of those records is lost. Main Window/frame 3 cannot claim Font
dialog pixels as a main-window screenshot. Reported OpusApp ownership is preserved.
Zero-control recorded windows and identical-looking variants remain inspectable.

## Stage 4 / PR unit: raw editor parity

### To do

- [x] T08: Use the same window crop and transform for Window, Controls, Overlay,
  Structure and Structure Overlay; expose full-frame pixels as supporting evidence.
- [x] T09: Provide source/attempt inspection and truthful record/renderable counts;
  keep offscreen/invalid records inspectable without misleading canvas geometry.
- [x] T10: Share hierarchy/topology family selection, show Window/Variant details,
  add an explicit skip-empty option and separate supplementary evidence from normal
  observation navigation. Remove scene/prediction/edit controls from the raw view.

### Acceptance

Colors/frame 21 shows only its dialog crop. Missing or partial coverage is explicit.
Two reads of 234 records are two attempts, not 468 distinct widgets. All five modes
agree on coordinates under moved windows, DPI, clipping and negative monitor origins.
Rapid/slow selection changes cannot leave stale image/card/properties combinations.

## Stage 5 / PR unit: grid, storage and live qualification

### To do

- [x] T11: Preserve mapped-grid registration/host binding, schema, native viewport
  capture, retained image tiles, row/column stitching, coverage, operation gates,
  human takeover, movement bounds, restoration and Excel export contracts.
- [x] T12: Qualify finish-map, merge, save, close/reopen, legacy import, invalid
  geometry, unique evidence, privacy, semantic hashes and atomic saves.
- [x] T13: Run live Word initial/action/popup-close/Font-tab/Colors scenarios and
  inspect the actual resulting raw variants in the editor. Include a slow provider,
  interruption and a grid-preservation scenario. Compare against test5 behavior.
- [x] Build the normal Visual Studio output, verify the running binary, and update
  this document and draft PR with results and any explicit qualification limits.

### Acceptance

Focused regressions and relevant grid/storage suites pass. Broader failures are
compared against the unchanged baseline before being classified as regressions.
A successful build is not a substitute for live recording/opening evidence. Active
grid sweeps keep their own ordered viewport observations and never become one
purportedly frozen frame. Source recordings are unchanged.

## Dependencies, delivery and rollback

Stages 2 and 3 use stage 1; stage 4 uses stage 3. Grid-preservation checks begin at
stage 1 and continue throughout. Stage 5 is the final acceptance gate. Implement on
the existing draft branch; stage only task changes, preserving pre-existing edits.
Use isolated build outputs during development so open editors stay usable, then
normal Visual Studio Build/Rebuild for the user-facing executable.

Keep additive readers and original bundles so a previous binary can still read
legacy evidence. Roll back an implementation stage by reverting its scoped commit;
never repair a capture failure by mutating the original recording. Do not silently
fall back to cross-frame reconstruction if a new capture is incomplete.

## 22:47 video follow-up

The [menu-context and label repair](2026-10-11-raw-menu-context-and-labels.md)
documents the remaining captured-menu, provider-provenance and canvas-label
issues, the bounded repair, and its acceptance checks. It extends this rollout.

## Execution log

- 2026-10-11: Authorized rollout documented before implementation. Existing working
  changes were inventoried and copied to private scratch for comparison. No unrelated
  changes are part of this rollout.

### Implementation delivered

- Additive `raw-observation/2` metadata records acquisition/seal times, capture
  method, stability, per-window pixel coverage and source-attempt provenance.
  Legacy absence remains unknown. Late responses cannot amend sealed evidence.
- Attended capture now waits for one full passive acquisition, with a 180-second
  native-provider budget, fair shares across pending windows/sources, a maximum
  of 35 seconds per provider attempt and 12,000 nodes per read. Pixels are captured
  before providers; OCR and geometry use those same pixels. Unconfigured sources
  have explicit outcomes. This is a bounded profile, not a guarantee every Windows
  provider will respond. Existing explicit Auto flows retain their separate probes.
- Stability checks compare input, native-window topology/state and pixels. A changed
  observation is retained and retried once; the second attempt remains incomplete
  if it changes again. Readiness follows sealing. Retention-limit truncation also
  marks the acquisition incomplete even when individual providers report success.
- Native surfaces come only from recorded Win32 inventory. OCR/geometry and
  unresolved native records use typed source-evidence groups. Reported ownership
  and duplicate attempts survive unchanged. Same-class HWNDs in the same frame
  remain separate variants, labelled with title and handle.
- Raw AppMap uses one selected-window crop/coordinate transform in all five modes.
  It exposes individual provider attempts, full raw JSON, window/variant details,
  offscreen visibility, supplementary records and skip-empty filtering. The initial
  attempt favors usable records rather than always choosing UIA Control. Scene,
  prediction and editing controls are hidden at this level.
- No grid acquisition/export implementation was replaced. Grid sweeps still retain
  their own ordered viewport captures; they are not folded into an attended frame.

### Recorded evidence and storage qualification

- Rebuilt the original 53-record Word bundle without modifying it. All 11,548 raw
  records compare field-for-field by frame and stream after graph save/reopen, with
  zero missing/extra records. This includes 4,526 OCR records and all 46 sentinel
  rectangles. OCR no longer creates a native HWND-0 family. The rebuilt graph has
  14,469 nodes and 20,258 edges and passes integrity validation.
- A fresh blank Word document was recorded through the normal Debug executable.
  Initial observation, font popup, popup closure, Font dialog, manual Advanced-tab
  selection and return, color popup, and Colors were exercised. No automatic tab
  traversal occurred. Finish Map succeeded: 11 screenshots, 7/7 complete interactions,
  no missing screenshots, 13,615 nodes and 17,199 edges. All 11,619 raw provider records
  survive field-equivalent comparison after save, with zero missing/extra records
  and no overlapping observation acquisition intervals. SQLite validation passes.
- The font popup demonstrates why source choice matters: UIA supplied one popup
  record while MSAA supplied 21. Colors supplied 18 UIA Raw, 18 UIA Control, 13 UIA
  Content and 91 MSAA records. The editor keeps these as distinct attempts.
- The live run retained six partial/interrupted scans. Font UIA reads timed out in
  several attempts, while MSAA retained 236 dialog records. Advanced subsequently
  returned all eight native reads successfully. Opening Colors during the preceding
  acquisition produced `target-changed-during-collection` and a fresh three-window
  attempt. That retry also failed the stability check and remains explicitly
  interrupted; it is not claimed to be an atomic or complete capture.
- Private recordings, screenshots, TRX logs and the annotated video report remain
  local evidence. No private captured content is committed. test5 remains unchanged.
- Resumed the same map for a second attended session. It captured a fresh complete
  baseline instead of borrowing the earlier session's baseline. Finish Map merged
  successfully: 12 screenshots, 14,529 nodes, 18,484 edges and all 12,492 raw records
  across both bundles, with zero missing/extra records. The merged map validates
  and opens in the staged editor. The first bundle's SHA-256 remains unchanged.
- The editor displays Font and Colors as separately labelled HWND variants in the
  same native family. Colors Window/Overlay uses its 340 by 367 crop; its selected
  MSAA attempt shows 91 records, while UIA, timeouts and unconfigured sources remain
  individually available. The interrupted acquisition status remains visible.
- Final UI inspection found a family-reselection reset: topology selection cleared
  the selected Colors variant and chose an earlier Font frame. The repair preserves
  the exact variant within the selected family and clears it when leaving that scope.
- Build 64 was verified against the original recording in the live editor. Colors
  frame 21 shows only its dialog crop and honestly retains zero source controls.
  Reselecting its topology family keeps Colors/frame 21. Main Window/frame 3 shows
  the recorded Font-sized pixel region at its original window-local position, with
  the remainder blank and `Window pixels: partial`; it no longer stretches dialog
  pixels into a fabricated full-window screenshot.

### Automated qualification and limits

- A clean copy of the committed task changes builds the full Release solution with
  zero warnings/errors. Only `global.json` was adjusted in that disposable copy to
  use the installed SDK feature band; the repository's existing SDK edit remains
  outside this PR. The normal working-tree build includes the user's pre-existing
  recorder changes, so its live evidence is reported separately from the clean tests.
- Clean Windows suite: **531/531 passed**. Clean Core suite: **450/454 passed**.
  All four failures reproduce on the unchanged pre-task HEAD: mapper overlay
  inspector placement, plus the three opaque-grid traversal/restoration cases.
  They remain release gates, not regressions attributed to this change.
- The native UIA table/header capture fixture passes both theory cases. These
  fixtures and grid contract suites qualify preservation of existing behavior;
  this is not a new live customer-application grid/export certification.
- Focused follow-up after the final readiness-label change: **87/87 passed**.
  New raw-seal tests: **3/3 passed**. Provider/window/crop/round-trip parity cases:
  **14/14 passed**. Broader graph/reader regressions previously passed **128/128**.
- After the family-reselection repair, the focused reader, parity and recorder
  command suites pass **115/115** and the full clean Release solution builds with
  zero warnings/errors. This follow-up covers the final source change; the full
  suite counts above precede the final readiness-label and selection-only changes.
- Normal Visual Studio Build produced **UI Atlas Core 2.0.65**, with seven projects
  built, seven up-to-date and zero failures. The live initial recording used build
  62; resume/merge used build 63; legacy crop/selection verification used build 64.
- A final startup check found the initial derived view preselected the raw selector's
  combined option. Raw provider initialization now runs only in the raw view, so
  first entry can choose its most useful available attempt. Clean Release build
  and the **115/115** focused run pass again after this final one-line UI condition.
- Recorder-only Build, repeated Build repair and Rebuild all stage a complete,
  matching viewer. Repository-boundary checks and the offline synthetic flow
  (build, validate, save, export/import, reopen and semantic diff) pass.
- A working-tree-only boundary failure came from an absolute local path in a
  pre-existing untracked document; the clean committed snapshot passes. A native
  table fixture affected by concurrent desktop activity passed on a quiet rerun.
  Neither unrelated document content nor the native failures were hidden or edited.
