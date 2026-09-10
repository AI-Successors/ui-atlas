# Excel recording golden

`excel-reference.json` is the deterministic control-layout baseline for the
`20260903-075704-excel-fb8b9c66.mlrec` recording. It stores one digest per raw
frame plus control, type, and visual-role counts. Frames 34, 35, and 38 are
manually reviewed examples; the remaining frames are provisional but are still
compared exactly so an extraction change cannot silently alter them.

Run the check from the repository root after building `UiAtlas.Core.Cli`:

```powershell
$recording = Join-Path $env:LOCALAPPDATA 'UiAtlas\Core\recordings\20260903-075704-excel-fb8b9c66.mlrec'
powershell -NoProfile -ExecutionPolicy Bypass -File tools\Test-RecordingGolden.ps1 -Recording $recording
```

Only use `-Update` after reviewing every reported frame change against its
screenshot. The check also summarizes popup accessibility fallback reasons,
which distinguishes provider timeouts from completed but empty UIA trees.
