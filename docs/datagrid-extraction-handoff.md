# DataGrid extraction handoff

Task 3 supplies physical viewport description and literal transcription of accepted capture regions. The implementation builds and has focused synthetic checks. **The selected Abacre reading mechanism remains unqualified.** The saved one-viewport comparison failed; this implementation must not authorize traversal or report complete business data from that failed gate.

## Owned files

- `src/UiAtlas.Core.Contracts/DataGridExtractionContracts.cs`
- `src/UiAtlas.Core.Recording.Windows/DataGridNativeProbe.cs`
- `src/UiAtlas.Core.Recording.Windows/TableCellOcrReader.cs`
- `src/UiAtlas.Core.Recording.Windows/TableGridReader.cs`
- `src/UiAtlas.Core.Recording.Windows/TableViewportDescriber.cs`
- `src/UiAtlas.Core.Recording.Windows/WindowsOcrTextRecognizer.cs` (modified: separate literal OCR operation; existing label operation retained)
- `tests/UiAtlas.Core.Windows.Tests/DataGridExtractionTests.cs`
- `docs/datagrid-extraction-handoff.md`
- `provenance/files.csv` (append the owned-file rows; do not replace another task's ledger)

Public dependency handoffs borrowed unchanged: Task 1's `DataGridMappingContracts.cs` (including optional record-ID key and explicitly reviewed uniform-blank qualification), and Task 2's `DataGridAcquisitionContracts.cs`. Task 2 owns worker dispatch, binding, capture, accepted placement, scrolling, restoration, and shared input files. Task 4 owns integration and independent expected data.

## Service signatures

```csharp
Task<GridViewportDescription> TableViewportDescriber.DescribeAsync(
    GridViewportRequest request, CancellationToken cancellationToken = default);

Task<GridReadResult> TableGridReader.ExtractAsync(
    CapturedGrid capture, GridExtractionOptions? options = null,
    CancellationToken cancellationToken = default);

GridNativeProbeResult DataGridNativeProbe.Collect(long hostHwnd, int maxNodes);
```

Run the native probe only in Task 2's killable UIA worker. It inspects container Grid/Table first, then bounded descendants and diagnostic legacy evidence. Unsupported native counts remain null, never an inferred empty table. Diagnostic legacy text does not establish cell associations.

Request header/body rectangles are original image pixels. Schema column intervals are global table-content pixels; fragment intervals include the request's `OffsetX`. Row bands come from observed rules, including explicit partial bands. Unruled canvas does not manufacture additional rows. A schema fragment remains incomplete. Fixed-schema validation does not silently replace saved labels.

Extraction consumes only accepted tiles and complete row regions. Every participating join needs one candidate, positive pixel evidence, and displacement equal to the actual tile-offset difference. Source IDs, image hashes, dimensions, row identity, row geometry, and physical column assignments are checked before transcription. Conflicting observations become unreadable. Record IDs remain ordinary data independent of acquisition-local occurrence IDs; duplicate IDs are reported and retained.

Default bounds are 200 rows, 32 columns, 30 seconds, and eight rows per batch. Cells return `Text`, `Empty` with an empty string, or `Unreadable` with null text and a reason. Source tile/hash/pixel rectangles survive all outcomes. Capture, extraction, restoration, timing, and the original failed capture stage are reported separately.

The local OCR operation uses individual cell crops at original resolution or bounded bilinear enlargement with padding. It does not downscale mosaics, normalize words, remove repetitions, infer punctuation, parse business types, or consult expected values. OCR silence is unreadable. An exactly uniform opaque cell is empty only when the selected detector has explicit qualification; otherwise the reason is `uniform-blank-detector-unqualified`. All other OCR failures remain distinct.

## Verification and limitations

Run locked restore, Release build, and all tests using the README commands. The 28 focused extraction cases cover duplicate labels, repeated words, blank versus unreadable, missing columns, shifted placements, duplicate source IDs, accepted horizontal overlap, conflicting text, row batch boundaries, partial rows, unruled canvas, cancellation, and capture-stage preservation.

The final saved-source check used one original exact-host image, 20 visible rows, and 10 visible columns. Task 4 independently transcribed the reference outside runtime acquisition. Comparison found zero missing, extra, or duplicate record IDs, but 70 incorrect cells: 43 wrong text values and 27 unreadable cells. Thirty-six uniform empty cells were correct; two additional expected blank cells remained unreadable. The initial-viewport question comparison had one false positive. No full-table business answer or two-axis live success is established.

An isolated decimal crop improved with bilinear enlargement, but the same complete viewport still failed. That isolated improvement does not qualify the reading path. Native Grid/Table/GridItem/TableItem/Value/Text support was absent in the observed probe; legacy support alone does not prove a table reader.

The final bounded legacy-text diagnostic attempt timed out at three seconds and the worker was killed. Its null counts/text are an explicit probe failure. No additional live reader calls or tuning were attempted after that final gate.

Source PNGs, cell crops, independently prepared expectations, mismatch details, and source/DLL hashes are local diagnostic evidence under the ignored `artifacts/datagrid-task3` directory. They are excluded from the public source handoff. The runtime has no reference to those expected files or to the independent verifier. No app data was edited, no automated scrolling was performed by Task 3, and no alternate OCR provider was added.

## Subsequent authorized table-image exploration

The user subsequently authorized the simplified floating explorer end to end. The revised [requirements](specs/datagrid-demo/task-3-extraction.requirements.md) and [implementation/evidence](specs/datagrid-demo/task-3-extraction.implementation.md) govern that extension. The preceding OCR qualification and no-scroll statements describe the earlier extraction handoff only.

The current worktree integrates the public mapper inspector and bounded capture helpers, adds an explicit image-only acquisition mode, and connects Explore/Stop to table/header identification and a stitched image preview/export. Current cell OCR and business-reading qualification are unchanged. Complete image capture is not complete structured data.

Release build and all 764 tests pass. Actual UIA and opaque Windows fixtures exercise Explore through result; the opaque fixture also verifies both axes, restoration, and Stop/partial output. Local verification artifacts are in `artifacts/table-explorer`. The original public checkout and peer worktrees were not modified; there is no commit, push, or publication. No current Abacre traversal or business answer is claimed.
