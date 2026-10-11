# Raw Data Streams capture and editor parity rollout

Status: authorized, implementation in progress (2026-10-11).

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

- [ ] T01: Introduce observation acquisition metadata and typed stream provenance;
  distinguish native automation, image-derived records, pixels and window inventory.
- [ ] T02: Add small synthetic/redacted fixtures for dialog-only pixels, popup
  evidence, overlapping intervals, duplicate attempts, HWND-0 OCR and invalid bounds.
- [ ] Preserve bundle backward compatibility and semantic hash round trips.

### Acceptance

One sealed observation has its own coverage and terminal stream outcomes. Raw
entries remain byte-equivalent in relevant provider fields after save/reopen.
Old metadata absence is represented as unknown, not guessed complete. Tests reject
cross-frame substitution and wrong-window image association.

## Stage 2 / PR unit: coordinated full observations

### To do

- [ ] T03: Route initial/manual/root/dialog/popup observations through a common full
  capture boundary; retain specialized probes only as explicit supporting work.
- [ ] T04: Replace the four-second/700-ms rich sweep with a bounded full profile,
  per-provider deadlines and progress. Avoid starving owned windows behind the root.
- [ ] T05: Check input/window topology/bounds/DPI/state changes across acquisition;
  retain interrupted attempts and recover with a bounded fresh observation.
- [ ] Keep ordinary same-looking observations; do not use visual deduplication to
  erase accepted manual observations. Publish readiness only after sealing.

### Acceptance

The frame-7/8 overlap cannot be accepted as synchronized. Opening a dialog or popup
does not replace the root inventory with a point probe. Slow and cancelled reads
have honest statuses; late responses cannot amend sealed evidence. Human input is
not blocked, and manual capture does not trigger active tab/grid exploration.

## Stage 3 / PR unit: native-window and stream projection

### To do

- [ ] T06: Stop turning image-source HWND 0 into a native surface. Preserve OCR and
  geometry in typed source evidence and associate pixels only through provenance.
- [ ] T07: Keep window-family and frame/window-instance identity separate; propagate
  exact pixel coverage, class/style/ex-style, source counts and capture health.
- [ ] Keep real unknown native ownership in diagnostics, without guessing a popup.

### Acceptance

No synthetic native-window family is created for this recording's 4,526 OCR
records, and none of those records is lost. Main Window/frame 3 cannot claim Font
dialog pixels as a main-window screenshot. Reported OpusApp ownership is preserved.
Zero-control recorded windows and identical-looking variants remain inspectable.

## Stage 4 / PR unit: raw editor parity

### To do

- [ ] T08: Use the same window crop and transform for Window, Controls, Overlay,
  Structure and Structure Overlay; expose full-frame pixels as supporting evidence.
- [ ] T09: Provide source/attempt inspection and truthful record/renderable counts;
  keep offscreen/invalid records inspectable without misleading canvas geometry.
- [ ] T10: Share hierarchy/topology family selection, show Window/Variant details,
  add an explicit skip-empty option and separate supplementary evidence from normal
  observation navigation. Remove scene/prediction/edit controls from the raw view.

### Acceptance

Colors/frame 21 shows only its dialog crop. Missing or partial coverage is explicit.
Two reads of 234 records are two attempts, not 468 distinct widgets. All five modes
agree on coordinates under moved windows, DPI, clipping and negative monitor origins.
Rapid/slow selection changes cannot leave stale image/card/properties combinations.

## Stage 5 / PR unit: grid, storage and live qualification

### To do

- [ ] T11: Preserve mapped-grid registration/host binding, schema, native viewport
  capture, retained image tiles, row/column stitching, coverage, operation gates,
  human takeover, movement bounds, restoration and Excel export contracts.
- [ ] T12: Qualify finish-map, merge, save, close/reopen, legacy import, invalid
  geometry, unique evidence, privacy, semantic hashes and atomic saves.
- [ ] T13: Run live Word initial/action/popup-close/Font-tab/Colors scenarios and
  inspect the actual resulting raw variants in the editor. Include a slow provider,
  interruption and a grid-preservation scenario. Compare against test5 behavior.
- [ ] Build the normal Visual Studio output, verify the running binary, and update
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

## Execution log

- 2026-10-11: Authorized rollout documented before implementation. Existing working
  changes were inventoried and copied to private scratch for comparison. No unrelated
  changes are part of this rollout.
