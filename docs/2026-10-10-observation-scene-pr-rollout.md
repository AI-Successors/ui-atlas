# Observation evidence and reconstructed scenes: PR rollout

Status: implementation and local qualification completed on 2026-10-10 for draft PR #1; delivery and remaining release gates are recorded below.

## Problem and evidence

A retained 39-observation Word recording shows sparse/empty control projections, popup controls replaced by generic visual regions, and popup surfaces reporting zero observations. Its popup diagnostics report native responses of 21, 25, 83, and 14 nodes with zero timed-out calls, followed by `content-incomplete`. Rejected responses were not retained. Screenshot-less deltas reference the initial baseline, and merged graph controls accumulate contradictory selected/enabled values without frame scope. The desktop can independently repair controls during rendering.

The video, native recordings, screenshots, and document contents are private qualification inputs and must remain outside Git. Existing bundles/maps and unrelated working-tree changes must be preserved.

## Contract

1. Retain bounded provider observations before readiness/normalization decisions. A rejection prevents promotion, not evidence retention. Distinguish timeout, missing root, disconnected descendants, wrong ownership, and empty content.
2. Use a shared, deterministic scene projection for graph construction and inspection. Full observations, partial observations, and deltas have distinct coverage. Carry-forward preserves source observation identity and never fabricates fresh evidence.
3. Keep control identity separate from observation-specific bounds, state, source, ownership and validity. Raw reported ownership is immutable; effective ownership requires compatible geometry and temporal evidence. Unresolved ownership stays unresolved.
4. Keep screenshot origin/time explicit, including inherited pixels. No stale baseline is silently described as the current screen. A partial observation does not prove disappearance.
5. Visual enrichment is a versioned build-time projection over immutable evidence. Rendering consumes the saved graph; it does not create substitute controls or change counts.
6. Popup content instances retain browsable observations. Full scene states and capture events remain distinguishable. Properties, AppMap and the inspected layer agree.
7. Existing version-1 recordings/graphs remain readable. Optional additions are backward compatible; replay may change derived graph identities/hashes. No in-place user-map migration.

## Ordered rollout

### Stage 1 — Provider evidence and popup acceptance

Persist bounded, redacted native popup attempts with timestamps, source status, target and structured rejection diagnostics, including attempts without an accepted frame. Retain original provider ownership. Recover coherent popup roots when a provider root points to an external ancestor without permitting unrelated window contents. Record actual timeout status. Add acceptance/rejection and bundle validation/privacy regressions.

Fresh capture identified a concrete additional cause: MSAA compaction removed equivalent parent nodes without redirecting their descendants. Compact equivalent nodes with a canonical ID map and rewrite parent references through that map. Preserve differences in selected/enabled/visibility state and never reconnect genuinely unknown parents.

### Stage 2 — Shared observation scene projection

Add a platform-independent scene reducer with explicit observed versus retained controls, snapshot/delta coverage, source frames, effective ownership and screenshot provenance. Wire the graph builder to this contract and expose it to readers. Update adaptive screenshot baselines after captured root states; require compatible surface geometry for inherited pixels.

### Stage 3 — Observation-scoped state and ownership

Persist frame-scoped control state alongside stable identities. Project selected/enabled/visibility/name/bounds from the requested observation, including merged sessions. Retain reported/effective owner and resolution reason. Preserve pre-existing underlying controls when popups appear, and never assign an unresolved control to an arbitrary surface. Extend graph validation and round-trip tests.

### Stage 4 — Derived visual enrichment

Run popup OCR/geometry enrichment in the existing offline build path, including visual-only popup frames currently skipped by recovery gating. Record derivation version and source frame. Keep original RDS separate. Remove desktop rendering-time repair and use the saved derived controls for every consumer.

### Stage 5 — KG viewer consistency

Expose popup observations, capture kind, partial coverage and inherited screenshot source. Resolve observation-specific control properties and render coverage gaps explicitly. Keep the inspected layer synchronized while retaining the pipeline horizon. Distinguish aggregate counts from selected-observation counts. Preserve keyboard navigation and `(BundleId, FrameSequence)` identity.

### Stage 6 — Qualification and PR delivery

Run focused provider/scene/state/reader regressions, full Core and Windows suites, isolated Release solution build, repository-boundary checks and offline smoke. Replay the retained Word recording in an isolated catalog and verify original hashes, raw evidence preservation, popup observations, scoped state, deterministic rebuild and SQLite round-trip. Inspect the rebuilt viewer against that catalog. Use a disposable Word document for fresh capture if available; never alter the user's open documents. Record unavailable or failed gates accurately, compare unexplained test failures with the pre-change baseline, and keep the PR draft if release gates remain open.

## Acceptance checklist

- [x] Rejected native responses remain inspectable without becoming trusted controls.
- [x] Popup root recovery accepts externally parented coherent provider trees and rejects unrelated content.
- [x] Timeout state matches actual provider outcomes.
- [x] Snapshot/delta/partial scenes retain provenance and never imply unobserved disappearance.
- [x] Same control can be selected in one observation and unselected in another after persistence.
- [x] Popup ownership preserves stable underlying controls and reports unresolved cases.
- [x] Derived popup enrichment is persisted; RDS and source bundle bytes remain unchanged.
- [x] Viewer rendering does not discover controls or alter graph counts.
- [x] Popup observations and screenshot source are visible; inspection layer is consistent.
- [x] Focused/full tests, build, boundary and smoke results recorded.
- [x] Private replay, viewer inspection and any fresh live qualification recorded separately.
- [x] Final diff reviewed, task changes committed/pushed, PR updated with remaining limits.

## Recovery

Keep the sealed recording and previous maps. Build new maps in a separate catalog, validate and inspect them before adoption. Old executables/maps remain usable. Rejected provider payloads missing from historical recordings cannot be recovered by replay; a fresh capture is required for those diagnostics.

## Execution record

The source review and initial working-tree snapshot preceded implementation. Existing build, manual-dialog and hook-lifecycle edits are user-owned and excluded from this change unless required and explicitly documented. test5 is a read-only behavioral reference; implementation targets UI Atlas and removes neither its historical evidence nor its separate runtime.

### Automated qualification

Qualification uses a checkout of the proposed index, excluding unrelated working-tree edits, and isolated build outputs. The installed SDK 10.0.401 was invoked explicitly through MSBuild because the committed SDK pin requests unavailable 10.0.200; the user's separate SDK-pin edits remain untouched.

| Check | Result |
| --- | --- |
| Release solution build | Passed, zero warnings/errors |
| Full Windows suite, final collector fix | 522/522 passed |
| Full Core suite | 398/402 passed; four native desktop failures also fail before this change |
| Final scene, reader, evidence and curation checks | 56/56 passed |
| Repository boundary, proposed snapshot | Passed |
| Offline smoke, isolated outputs | Passed, including validation, SQLite, exports and round-trip |

The four Core failures are `MapperOverlayWindowTests.RealWindowOverlayRefreshSelectionCaptureAndTeardown` and all three `GridExplorerNativeWindowTests.ExploreOpaqueGridTraversesAndRestoresWithoutReadingText` cases. The pre-change Core run also had two failures locating the repository from isolated outputs; explicit repository-root support resolves those. One pre-change Windows timing failure did not recur in the final run. Suites run sequentially because desktop tests share native resources. No green release or hosted-CI result is implied.

### Historical replay and viewer

The private 39-frame Word recording was copied to a separate catalog; originals and their hashes were preserved. Rebuild validates with 3,408 nodes and 4,487 edges. Repeated final rebuilds produce semantic hash `8a8f943e6e3d1659da5542ab59cedbd0f0ac1b1e790aa9f97965a28fe1ce3b0d`. Exact observation comparison retains all 2,081 source controls in Raw Data Streams, including two controls from a no-longer-scoped HWND previously dropped. Their effective owner remains unresolved.

The four popup observations now expose persisted derived content: underline 5, highlight 20, font color 24, and line spacing 11 controls. These counts include containers and do not certify complete palette recovery. Nine raw control identities have differing selected state across observations; their selected-observation projections remain separate. SQLite persistence, cross-session frame identity and safe-export stripping are covered by tests.

The rebuilt viewer was inspected using the isolated catalog. Popup observation cards, frame-specific control counts, screenshot source, Raw World inspection indicator and historical absence of provider attempts were visible. Inspection found and corrected missing merged scope metadata, unrelated-surface retained counts and a generic container label overlapping popup text. The original user viewer and maps were preserved.

The final executable was also inspected against the fresh native recording: font-color frame 10 shows 83 controls, eight inspectable provider attempts, `popup-delta` coverage and zero retained controls from unrelated surfaces. The properties label distinguishes projection scope from the recorded capture coverage shown with its pixels.

### Fresh Word capture

A blank disposable Word document was used without entering document content. Before the MSAA compaction correction, the new evidence stream retained all 25 rejected attempts: twelve with 21 returned underline controls and thirteen with 14 line-spacing controls. Every attempt reports `disconnected-content`, zero accepted controls, no timeout and no truncation. The 294,887 retained bytes made the dangling-parent defect directly inspectable.

After correction, a second recording captured all four popups natively: underline 21 controls, line spacing 14, highlight 25, and font color 83. Both verification responses for each popup were retained and fully accepted: eight attempts, no timeout/truncation, and zero missing parent references. Names include underline styles, spacing values and native color names. The sealed recording builds to a valid map with 940 nodes and 1,249 edges. Its quality report has zero partial screen scans and zero missing screenshots, but four of eight interactions lack a confirmed result screen. The menus were inspected and dismissed without choosing formatting commands; this qualifies capture and build, not arbitrary action replay.

### Remaining gates

- Keep the PR draft until native desktop test failures and hosted CI are resolved or explicitly accepted by maintainers.
- Historical recordings cannot recover native responses discarded by older executables. Visual palette reconstruction remains incomplete and is labeled derived evidence.
- Partial root scans and unconfirmed interaction outcomes remain review findings; popup acceptance alone is not proof of unrestricted Word workflow reliability.

### Delivery

Implementation commit `94a9c8e` was pushed to the existing `codex/word-recording-hierarchy-repair` branch. [Draft PR #1](https://github.com/AI-Successors/ui-atlas/pull/1) now describes the combined hierarchy, provider-evidence and scene-reconstruction changes with these validation limits. The GitHub connector could read but not edit PR metadata; the update succeeded using the repository's existing Git credential through GitHub's normal API. No new credentials or permissions were created.

Hosted checks started for the implementation commit; their result remains a separate release gate. The branch is not merged. Private qualification artifacts stay outside Git, and all pre-existing unstaged/untracked paths are preserved.
