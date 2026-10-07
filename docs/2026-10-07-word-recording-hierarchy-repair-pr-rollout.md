# Word recording hierarchy and evidence repair PR rollout

Status: implemented and qualified with the limitations below; ready for PR review. Execution authorized on 2026-10-07.

## Problem and proven regression

A retained Word recording fails final graph validation after Draw/Home ribbon observations, a Styles dialog capture, and another Home/Font observation. The original bundle is valid. A four-observation reproduction establishes the failure without OCR or live desktop interaction.

Historical observation replay passes at predecessor revision `ba30255` and fails at its immediate successor `24cba7c` (control-delta native-chrome retention). The first public UI Atlas revision `a478ce5` also fails. The older shared-control identity algorithm assigns duplicate parent suffixes after descendant paths have been calculated. Retained tab-specific ancestors and a dialog capture that clears the main-window cache expose that weakness.

The investigation also found carried controls attributed to an observation that did not capture them. The repair must correct provenance, not merely suppress graph validation errors.

## Scope and invariants

- Preserve sealed recording bytes and existing published maps. Write qualification output to a separate local directory/catalog. Never commit private recordings, screenshots, document contents, or machine-local paths.
- Keep Raw Data Streams tied to original capture observations. Keep derived/enriched observations separate and retain the original frame for carried-control evidence.
- Carrying a control must not establish fresh visibility, confirmation, state membership, or an interaction target observation.
- Restrict retained native chrome to persistent structure; tab-specific menu items/ancestors are not permanent chrome. Invalidate only sufficiently observed windows and conflicting content context.
- Resolve child identities from resolved parent identities. Duplicate appearance, disappearance, and enumeration order must not reparent a merged node. Ambiguous observations must remain separate rather than silently merging.
- Preserve strict graph validation, offline operation, existing bundle compatibility, and the recorder finish/open/resume workflow.
- Existing maps remain readable. Rebuilding may produce different graph/control IDs because identity and evidence were corrected; no in-place migration of existing maps is planned.

## Ordered PR work

### 1. Reproduction and evidence contract

Add synthetic regression cases for the four-stage ribbon/dialog sequence, duplicate parents and descendants, changing duplicate membership/order, partial toolbar capture, scoped cache reset, and original-versus-derived observations. Prove the primary regressions fail before implementation. No private fixture enters the repository.

### 2. Provenance and scoped retention

Separate original observations from curated graph input. Preserve source frames for retained controls, including existing Excel retention. Materialize raw streams before curation, avoid stamping retained controls with the current frame, and exclude retained assumptions from observed-state membership. Bound cache retention to persistent ancestry and actual observed-window coverage.

### 3. Consistent hierarchy identity

Construct parent identities before child identities. Use deterministic discriminators independent of a frame-local duplicate rank; scope genuinely ambiguous identities to their observation evidence. Preserve meaningful unnamed-container distinctions such as ribbon page names. Reject unexpected identity/parent conflicts instead of publishing contradictory edges.

### 4. Qualification

Run focused regressions, all repository tests, Release build, repository-boundary checks, and offline smoke. Replay the full retained Word bundle, including the CLI enrichment/save path, into separate output. Verify strict validation, save/load round trip, repeat-build semantic hash, Home/Draw/Font/Styles relationships, absence of fabricated raw-stream evidence, and unchanged source bundle hash.

Use the exact rebuilt executable for an attended fresh Word record/save/open/resume check. Record live proof separately from automated/source replay. Do not claim live acceptance if interaction tools or the desktop are unavailable.

### 5. Review and delivery

Review the final diff for identity stability, evidence provenance, cache invalidation, privacy, and compatibility. Update this document with commands/results, outstanding limits, and recovery instructions. Prepare the branch and PR review material; retain any unavailable external publication step explicitly as a delivery limitation.

## Acceptance gates

- [x] Synthetic Word reproduction fails before repair and passes after repair.
- [x] Duplicate parent/child identity tests cover disappearance and reordering.
- [x] Native/Excel retention preserves original evidence and does not create current observation claims.
- [x] Raw streams preserve actual captured controls even when higher-world curation removes or adds controls.
- [x] Partial/delta and dialog-only observations obey window coverage boundaries.
- [x] Release build, repository boundary, and offline smoke pass.
- [ ] Full suite is green: four desktop failures reproduce on the untouched base revision (details below).
- [x] Private Word replay, deterministic rebuild, persistence, hierarchy, and provenance checks pass.
- [x] Fresh Word finish/open/resume qualification is recorded with the executable identity and coverage limitations.
- [x] Code review and qualification documentation are complete; deliver as a draft PR with the unresolved gates visible.

## Execution record

### Implementation

- Original capture observations are supplied separately from offline-enriched observations. Raw Data Streams retain captured duplicates and exclude carried controls. Raw package identities include session and native window identity to prevent collisions when recordings are merged.
- Retained observations keep their original frame, bounds, visibility and extraction evidence. They cannot establish current-state membership or supply a missing interaction source observation.
- Native chrome requires persistent ancestry. Incomplete and node-limited scans retain eligible chrome only in the observed window; complete captures clear only observed windows. Geometry changes and conflicting ancestor context invalidate retention.
- Control paths resolve the final parent path first. Named ribbon groups distinguish Home and Draw. Ambiguous siblings use session-scoped runtime identity rather than frame-local ranks; missing or duplicate runtime identities remain evidence-scoped. Ambiguous parent references are flagged rather than arbitrarily selecting a parent. An explicit internal guard rejects contradictory parent assignments; GraphValidator remains unchanged.

### Automated qualification

The five original regression cases failed before implementation (three containment failures and two incorrect-evidence assertions) and passed afterward. The expanded hierarchy/provenance tests plus GraphPipelineTests and RawSemanticWorldTests pass **94/94**. Coverage includes scoped cache clearing, original/enriched input, merged recording packages and missing interaction-source evidence.

Qualification used the pinned **.NET SDK 10.0.200**, installed in a private local qualification directory because the machine initially lacked the pinned SDK. No SDK pin or dependency was changed. Build-process cleanup was disabled to preserve user sessions.

```powershell
dotnet restore UiAtlas.Core.slnx --locked-mode -p:StopRunningUiAtlasProcesses=false
dotnet build UiAtlas.Core.slnx -c Release --no-restore -p:StopRunningUiAtlasProcesses=false
dotnet test UiAtlas.Core.slnx -c Release --no-restore -p:StopRunningUiAtlasProcesses=false
./tools/Test-RepositoryBoundary.ps1
./tools/Smoke-Offline.ps1
```

Release build: zero warnings/errors. Boundary checks and offline smoke: pass. Full solution run: Windows tests **518/518**, Core tests **388 passed / 4 failed**. Two further focused regressions were added afterward and pass in the 94-test final focused run.

The four desktop failures reproduce unchanged on an independently built archive of base revision `8f86b4f` using the same pinned SDK:

- `MapperOverlayWindowTests.RealWindowOverlayRefreshSelectionCaptureAndTeardown`: inspector outside target physical bounds.
- All three `GridExplorerNativeWindowTests.ExploreOpaqueGridTraversesAndRestoresWithoutReadingText` cases: expected mapper state not reached.

They remain a known baseline qualification limitation; this PR does not claim a green full suite or change those unrelated tests.

### Private fixture qualification

The original 25-frame bundle and all 12 interactions build successfully. The four-frame subset also passes. Direct replay produces 2,608 nodes / 3,378 edges; the CLI enrichment/save path produces 2,736 nodes / 3,546 edges. Different counts are expected because the CLI reconstructs additional controls from screenshots.

- Every one of the 1,629 raw control nodes matches its original runtime identity, bounds and frame; per-frame counts match the captured payload.
- Font in the two Home observations has one identity and a Home parent. No Draw node claims frame-11 evidence.
- Strict validation, repeated semantic hash, and SQLite save/load pass. A second CLI build preserves semantic hash `d4fc5d493355b82fd4cfa9911c77d7b1ae99f74879ea589c3b5458f82ae93161`.
- Original bundle SHA256 remains `c18fcd13db555e3f840771e6e7fc98582ba2cee8b07cbf56f66f6aec8860dbf4`.

Private fixtures, local diagnostic harnesses, logs and databases stay outside the repository. No user map was replaced.

### Live qualification and remaining limits

Used a new blank Word document and a separate qualification catalog. The launched recorder was this checkout's Release `ui-atlas.exe`; the map viewer was this checkout's Release `UiAtlas.Core.Desktop.dll`. The viewer's Resume action was verified to launch the same Release recorder, rather than a Debug or installed copy.

- Recorder executable SHA256: `71b422fb87c3d3225de789ddb4c4600dec0aadadca869cb676773bc3dbea1884`.
- Loaded `UiAtlas.Core.Build.dll` SHA256: `1fef0fc422628a51d1f5c5e8f94c95dc19747da82bbfbf6558a2ac254934c432`.
- Fresh manual Home/Draw/Home/Styles navigation followed by Finish saved a valid map (390 nodes / 439 edges). It was opened in the map viewer; Resume started another manual recording, and Finish merged a second sealed bundle into the same logical map. The two-bundle map passed CLI validation.
- The successful fresh recording used hover discovery off. Its quality report remained **Needs review**: two partial scans and one of three interactions without a confirmed result screen. Resume was a baseline/matching capture without further clicks. These checks prove finish/open/append/save integrity, not complete UI coverage or a successful live recreation of every historical frame.
- The first live attempt failed during capture with `Automation window is outside the sealed target process` around Styles dismissal. This happened before graph building; the bundle was retained. Its cause has not been established, and disabling hover is not claimed as a proven fix. Keep this separate capture-stage issue visible for follow-up qualification.
- The recorder's Saved Maps list displayed the default library despite the isolated CLI catalog; the private map was opened by its explicit catalog ID. No default-library map was selected or modified.

The historical 25-frame replay is the deterministic acceptance evidence for the reported containment defect. The full-suite baseline failures and incomplete live capture coverage prevent a claim of unrestricted release qualification; the PR remains draft pending those gates or an explicit maintainer waiver.

### Recovery and compatibility

Existing saved maps remain readable. Rebuilds can change control/state IDs, raw package IDs and semantic hashes; consumers must not mix IDs from different revisions. Ambiguous controls may intentionally remain separate across sessions until stronger matching evidence exists.

Keep the original sealed recording. Build the repaired map in a separate catalog first; validate and open it before adopting it. Rollback consists of using the previous executable and existing map. Rebuilding this particular recording with the previous executable reproduces the original failure; deleting or rewriting the recording is not a repair.
