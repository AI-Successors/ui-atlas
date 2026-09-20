# Bounded DataGrid acquisition handoff

Task 2 implementation handoff, 2026-09-19. Public base: `c860f1ff395978189dfed8c33dacc8f319868379`.

The reusable capture service is implemented and synthetically checked. **Fresh live two-axis capture is not qualified.** No automated scrolling, application edits, additions, imports, or deletions were performed. The live viewport reader remained unqualified, and bounded scrolling approval was not granted. The later mapper attempt stopped at foreground loss; that is a safety stop, not a completed mapping.

This paragraph and the evidence below describe the original acquisition handoff. The combined worktree additionally includes the subsequently authorized [image exploration mode](specs/datagrid-demo/task-3-extraction.implementation.md), with Explore / Stop, bounded native/UIA traversal, and stitched-image verification against synthetic windows. Its structure-only mode is independent of OCR qualification; MappingSurvey/FreshRead retain their data-reading gate. Current Abacre traversal and a business answer remain unverified.

## Public service signatures

```csharp
new DataGridAcquisitionCoordinator();
new DataGridAcquisitionCoordinator(TableViewportDescriber describer);

Task<CapturedGrid> AcquireAsync(
    AcquisitionRequest request,
    IProgress<AcquisitionProgress>? progress = null,
    CancellationToken cancellationToken = default);

Task<GridViewportRequest> CaptureViewportAsync(
    MappedGridDefinition definition,
    GridTargetIdentity target,
    CancellationToken cancellationToken = default);

DataGridTargetBinding.ResolveTarget(MappedGridDefinition definition);
DataGridTargetBinding.ResolveTarget(MappedGridDefinition definition, long? selectedHwnd);
DataGridTargetBinding.DescribeLocator(long selectedHwnd, long hostHwnd);

UiaWorkerClient.ProbeGridAsync(
    WindowTarget target, long hostHwnd, TimeSpan timeout, CancellationToken cancellationToken);
```

Contracts are in `src/UiAtlas.Core.Contracts/DataGridAcquisitionContracts.cs`. Approval binds the selected grid, fresh window/process/host identity, axes, and exact limits. Missing, expired, or mismatched approval fails before any input. `LocalOcrQualified` defaults false and must reflect independent viewport verification. Saved schema, capture coverage, extraction, and restoration have separate meanings.

The capture is host-only at original screen resolution. Header, body, and row rectangles are image-local. Tile offsets locate the body in table-content pixels. Row occurrence IDs derive from verified placement and row pitch, never from cell text. Accepted join displacement equals the difference between source and destination tile offsets. Verified return tiles retain the anchor's row occurrences and remain connected to subsequent vertical joins.

## Owned file manifest

New files:

- `src/UiAtlas.Core.Contracts/DataGridAcquisitionContracts.cs`
- `src/UiAtlas.Core.Recording.Windows/DataGridAcquisitionCoordinator.cs`
- `src/UiAtlas.Core.Recording.Windows/DataGridTargetBinding.cs`
- `src/UiAtlas.Core.Recording.Windows/DataGridCaptureSession.cs`
- `src/UiAtlas.Core.Recording.Windows/DataGridOperationGate.cs`
- `src/UiAtlas.Core.Recording.Windows/DataGridRegistration.cs`
- `src/UiAtlas.Core.Recording.Windows/DataGridEvidenceStore.cs`
- `tests/UiAtlas.Core.Windows.Tests/DataGridAcquisitionTests.cs`
- `tests/UiAtlas.Core.Windows.Tests/DataGridRegistrationTests.cs`
- `tests/UiAtlas.Core.Windows.Tests/DataGridNativeBoundaryTests.cs`
- `docs/datagrid-acquisition-handoff.md`

Modified existing files:

- `src/UiAtlas.Core.Recording.Windows/UiaWorkerClient.cs`
- `src/UiAtlas.Core.Recording.Windows/UiaWorkerHost.cs`
- `src/UiAtlas.Core.Recording.Windows/ManualRecordingSession.cs`
- `provenance/files.csv` — merge missing entries; do not replace peer entries.

Public peer dependencies borrowed for compilation:

- Task 1: `src/UiAtlas.Core.Contracts/DataGridMappingContracts.cs` including optional record-ID column and blank-cell qualification fields.
- Task 3: `src/UiAtlas.Core.Contracts/DataGridExtractionContracts.cs`.
- Task 3: `src/UiAtlas.Core.Recording.Windows/DataGridNativeProbe.cs`.
- Task 3: `src/UiAtlas.Core.Recording.Windows/TableViewportDescriber.cs`.
- Task 3: `src/UiAtlas.Core.Recording.Windows/TableCellOcrReader.cs`.
- Task 3: `src/UiAtlas.Core.Recording.Windows/WindowsOcrTextRecognizer.cs`.

Take Task 1/3 dependencies from their owners' final handoffs. No package, lockfile, project, or solution dependency change belongs to Task 2. No private source or runtime is required. `SafeSyntheticInput.cs` was not changed: the new path sends bounded synchronous scroll messages to the sealed native host and does not generate untagged global pointer events.

## Behavior and bounds

The same per-user/session named semaphore covers mapper, MCP, and manual recorder lifetimes. Operations do not queue. The lease stays held through restoration. Native binding uses exact class/caption sibling ancestry, process start time, root ownership, geometry, visibility, foreground, occlusion, and point ownership checks. Empty native captions are exact values; treating them as wildcards caused a live round-trip failure that was repaired and retested.

The supported native path issues one `WM_HSCROLL`/`WM_VSCROLL` line request at a time. It does not infer displacement from native scroll coordinates. Each step captures pixels, checks layout, and accepts only one distinctive compatible body translation. Fixed headers and rule-only patterns cannot establish body displacement. Ambiguous, gapped, stationary-without-edge, cross-axis, and changed-range observations stop extension. Native coordinates only corroborate an unchanged edge or identify a position to restore.

The algorithm establishes left/top, sweeps each row band horizontally, verifies the return to its original left anchor, and then advances vertically. Original PNGs, hashes, placements, row regions, movement receipts, accepted/rejected joins, reasons, timing, limits, approval time, and separate restoration results are retained in a compact JSON manifest.

Default hard ceilings are 60 seconds, 32 movements, 24 retained tiles, and 64 MiB PNGs. Boundary probes, returns, and restoration count. Five seconds, sufficient return movements, one tile, and original-anchor PNG capacity are reserved for restoration. Short limits can fail before input. Cancellation permits only bounded safe restoration; human takeover, target loss, or unsafe scope prohibit restoration input. Restoration succeeds only when native position and original body/header pixels both agree. When no scrolling occurred, restoration is safely skipped without inventing an anchor-verification receipt.

## Verification

- Locked restore passed.
- Final Release solution build passed with zero warnings and zero errors.
- Final full suite passed: **712 tests** — 331 core and 381 Windows; zero failures or skips.
- The Windows total includes **29 Task 2 checks** for actual measured translations, short terminal moves, repeated-content ambiguity, rule-only rejection, cross-axis/gap rejection, two-axis assembly, distinct row occurrences, duplicate labels, approval, common and cross-process gates, limits, cancellation, takeover, target loss, foreground loss, independent restoration, typed worker timeout/output bounds, geometry safety, and exact empty-caption matching.
- `git diff --check` passed.

The oversized-output worker fixture passed alone after an integration run hit its five-second startup allowance. Non-timeout fixture allowance was increased to 15 seconds to keep output-quota assertions separate from process-start contention. Production probe deadlines and the dedicated 250 ms hung-worker test were unchanged. The final full suite passed with this test-only adjustment.

## Live observations and remaining gaps

Read-only public native inspection found Abacre 12, visible main window `TfrmMain`, hidden root owner, and `TAbacreDBGrid` under the Orders view. Fresh `DescribeLocator` -> `ResolveTarget` round-trip succeeded after the caption fix. Physical screen capture was used to avoid invisible-frame offsets and DPI virtualization in exploratory WGC captures.

The latest inspected Orders view showed twenty visible rows and ten headers, including Server. Vertical Win32 range suggested further records; the horizontal range was a coarse `0..127` with page size zero. Neither range proves continuity or actual pixel movement. The window at that observation was still wide enough to show Server.

The initial native container probe completed with Grid/Table/GridItem/TableItem/Value/Text unsupported and Legacy available; row/column counts remained unknown, not zero. The later optional literal Legacy probe timed out under its three-second budget and was killed. Task 3 independently reported OCR decimal loss and unreadable cells, so reading qualification was not granted.

Still unproven: actual Abacre line-scroll behavior, both-axis displacement and joins, live duplicate/terminal behavior, restoration against the real host, and the business answer through a complete live capture. A VCL line-scroll command may change selection before moving visible content; the current service fails explicitly if that does not yield a verified translation. No successful live traversal claim is supported by these synthetic checks.

Machine-local diagnostic artifacts remain ignored under `artifacts/datagrid-probe/`, including host PNGs, bounded native-probe JSON, the native ancestry round-trip receipt, and `final-source-hashes.json`. Do not include application captures or machine-local probe helpers in tracked source or release packages.
