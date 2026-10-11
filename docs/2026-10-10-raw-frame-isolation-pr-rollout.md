# Raw Data Streams: immutable observation rollout

Status: implemented on 2026-10-10; amendment to draft PR #1. Automated qualification
completed with the native desktop and interactive limitations recorded below.

## Correction to the previous rollout

Raw Data Streams answers what each provider returned during one acquisition. The
previous scene-context addition to this layer was incorrect. Scene reduction,
carry-forward, cached knowledge and inferred ownership belong to Raw World or
later projections. This document supersedes that portion of the observation-scene
and frame-geometry rollouts. It does not remove reconstruction from Raw World.

test5 is a read-only reference: its Full/manual path waits for the recorded
Win32/UIA observation before arming the next action, and its raw surfaces use
frame-plus-window identity. Its compatible-observation UIA backfill is explicitly
excluded from this contract.

## Contract

- One acquisition has one observation sequence. Its screenshot, native window
  inventory and provider responses carry source timestamps and completion status.
  Acquisition is bounded and sequential where providers require it; it is not an
  atomic freeze of the application process.
- Raw payloads preserve provider coordinates, ownership, duplicates and failed or
  empty responses. Status distinguishes unavailable, not requested, partial and
  timed-out streams. Missing evidence is never filled from another observation.
- Initial pixels and subsequent quick-scan reads are stages of the same frame.
  Point/control probes are supplementary capture records, not full observations.
- Available passive native streams are attempted within a bounded acquisition
  budget. Optional or unsupported collectors have explicit statuses; no remote
  model is enabled and no active exploration is introduced by this change.
- Each recorded window has a raw surface even with zero controls. Unknown provider
  ownership remains unresolved. Display-coordinate transforms may use only the
  selected observation's bounds. Stream selection does not merge provider output.
- The raw viewer never calls scene reduction or borrows earlier screenshots.
  Raw World retains its separate reconstruction and provenance behavior.
- Existing recordings remain readable and unchanged. Legacy deltas remain visibly
  partial; absent provider payloads cannot be recovered from later observations.

## Ordered PR rollout

1. Add optional frame-local raw stream metadata/payloads and supplementary record
   identity, bounded validation and privacy handling, preserving old bundle reads.
2. Commit quick acquisition once, retain original native responses, attempt the
   available passive collectors with explicit outcomes, and separate cached hints.
3. Build raw surfaces/controls from the original stream payloads; retain independent
   provider entries while continuing to build Raw World from its derived inputs.
4. Remove raw scene context and screenshot inheritance. Add stream selection,
   frame evidence inspection and clear supplementary/legacy capture labeling.
5. Exercise acquisition, provider failure, multiple streams, unknown ownership,
   geometry, isolation, persistence and reopening. Run focused and broader checks,
   inspect the viewer where possible, then amend the existing draft PR.

## Acceptance gates

- [x] One quick acquisition produces one observation with its own screenshot.
- [x] Available raw providers have retained payloads and explicit outcomes.
- [x] No cached or retained controls become fresh raw provider observations.
- [x] Raw frame N cannot display controls or pixels from frame N-1 or N+1.
- [x] Windows with zero controls and unresolved owners remain inspectable.
- [x] Independent streams have separate records and a viewer stream selector.
- [x] Supplementary probes have explicit record identity and card labels.
- [x] New stream evidence passes privacy, validation and immutable reopen checks.
- [x] Legacy recordings remain readable without in-place modification.
- [x] Test/build results and live verification limits recorded below.
- [ ] Interactive raw-stream switching and a fresh manual Word record/open run.

## Execution and qualification

The recorder now stages pixels, gathers provider responses, and seals one frame.
The in-frame collector is passive. Existing opt-in hover/focus discovery runs
after the baseline is sealed and the recording lock is released; popup captures
are separate frames and never amend the baseline. This avoids nested captures
deadlocking the recording lock.
Native UIA raw/control/content and MSAA reads have a shared four-second passive
budget; OCR has a two-second deadline. The manual root deadline is fourteen
seconds. These are bounded attempts, not a claim of complete application coverage.
Unavailable, cancelled, truncated, unconfigured and exhausted streams remain
explicit. Raw payload retention is capped at 256 streams and 40,000 controls per
frame; retained text passes the existing privacy redaction policy.

Raw graph construction namespaces provider identities independently, retains
empty window surfaces and unresolved owners, and uses original reported geometry.
The viewer exposes the selected frame's stream outcomes and JSON, removes raw
scene context and candidate styling, and never borrows another frame's pixels.
Raw World continues to use its separate derived observations and scene reducer.

| Check | Result |
| --- | --- |
| Working-tree isolated Release solution build | Passed, zero warnings/errors |
| Normal Visual Studio 2026 Debug solution build, final code | 14 succeeded, zero failed; recorder build 42 |
| Full Windows suite, working tree, before final lock-order review | 532/532 passed |
| Full Core suite, working tree | 428/434 passed; failures explained below |
| Core focused regression suite, working tree | 56/56 passed |
| Exact proposed snapshot Release build | Passed using installed SDK 10.0.401 explicitly |
| Exact proposed Core regressions plus repository tests, final code | 129/129 passed |
| Exact proposed full Windows suite, final code | 526/527 passed; capsule foreground case passes separately on retry |
| Exact proposed native observation capture tests | 3/3 passed: completed, cancelled and unavailable collector |
| Exact proposed repository boundary and offline smoke | Passed, including SQLite and export/reimport round-trip |

The full Core run has the same four native failures already reproduced before
this amendment: the mapper overlay lifecycle case and three opaque-grid traversal
cases. Two additional failures came from finding the repository in isolated
outputs. Supplying the repository root fixes discovery; the working tree's source
boundary check then encounters an unrelated existing build-hook edit. Both
repository tests and the boundary script pass against the exact proposed snapshot,
which excludes unrelated edits. The 532-test Windows total includes pre-existing
uncommitted tests; the three new capture tests also pass against the proposed
snapshot. This is not a claim that all release or hosted CI gates are green.

The final proposed Windows run's one failure is
`FloatingCapsuleTests.ApprovalRequiresClickAndActiveCapsuleDoesNotActivateOrOccludeTable`:
the fixture expected its HWND to be foreground but Windows returned zero. This
test and its capsule implementation are unchanged. The same proposed binary passes
the case when rerun separately; a clean build of prior commit `cdbff9a2` also passes
the isolated case. The failure was not reproduced, and no assertion was suppressed.

A private historical Word bundle was copied to a separate catalog and rebuilt:
928 nodes, 1,604 edges, valid semantic hash
`483dd4c6ab86bc6c2786bbeb8323485a5fa394f7eb03b8ca218bdf50d3d217b5`.
The proposed viewer opens the historical map with its evidence attached. Computer
Use clicks did not reliably switch its layers, so interactive raw-stream selection
is unqualified. No fresh manual Word record/save/open run was completed for this
amendment; the native capture tests use a disposable WPF window. Earlier live Word
results in the other rollout documents describe earlier revisions only.

Private evidence, original recordings/maps, test5, and unrelated working-tree
changes are preserved. The PR remains draft and unmerged while these gates remain
open.
