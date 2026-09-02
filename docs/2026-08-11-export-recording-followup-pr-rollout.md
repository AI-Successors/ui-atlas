# Export and continuous-recording follow-up PR rollout

Status: implemented and qualified on 2026-08-11.

## Acceptance changes

1. The persistent shell's identity-preserving export is `EXPORT MAP <map-id>`; `EXPORT UI-ATLAS` is no longer the documented command surface.
2. Graph JSON is visibly organized into App, Process, Raw Data Streams, Raw World, and Semantic World without losing canonical node/edge order or legacy flat-v2 readability.
3. After consent and baseline capture, one target click is armed automatically. After each completed Slow & Rich capture, the target remains active and another click is armed without requiring `N` or reactivating the console.
4. `N`, `T`, `D`, and `C` remain available through a concurrent console-command wait. Only clicks inside the sealed target root-owner scope count, and clicks during materialization remain deliberately unarmed.

## PR sequence

### PR 1 - Command grammar

- Make `EXPORT MAP <map-id>` and its validate/delete forms canonical in help, documentation, smoke coverage, and default filenames.
- Retain the old spelling only as an undocumented compatibility alias.

### PR 2 - Five-area JSON envelope

- Add `ui-atlas.uikg.export/1` with `app`, `process`, `rawDataStreams`, `rawWorld`, and `semanticWorld` areas.
- Preserve lossless round trips with explicit canonical order indexes and retain flat `ui-atlas.uikg/2` read compatibility.
- Add schema and regression coverage.

### PR 3 - Continuous attended recording

- Arm one target click automatically after the baseline and each completed capture.
- Race the active target-click wait with one persistent console-command wait.
- Never restore console focus after a mapping step; explicit `N`/`T` reactivates the target, while `D`/`C` terminates safely.

### PR 4 - Qualification and release

- Run all tests, full Release build, boundary checks, offline catalog/export smoke, and privacy scans.
- Produce two byte-identical 1.8.0 archives, run package compliance and extracted-package help/export smoke, and record the final hash.

## Definition of done

- All three acceptance changes are implemented and documented.
- Safe and full graph JSON show the five areas and round-trip losslessly.
- The offline flow exercises `EXPORT MAP`, validation, and deletion.
- No private recording content or machine-local identifier enters the repository or package.

## Execution result

- PR 1 made `EXPORT MAP <map-id>` canonical across help, documentation, default `-map.json` naming, validation/deletion, and the isolated smoke flow. The former spelling remains undocumented for compatibility.
- PR 2 added the `ui-atlas.uikg.export/1` envelope and schema. Safe exports retain only a non-sensitive area marker, full exports retain their evidence profile, canonical node/edge order survives grouping, and legacy flat-v2 JSON remains readable.
- PR 3 replaced per-step console restoration with concurrent command/click waiting. Live synthetic-target acceptance captured a click without `N`, kept the target foreground through materialization, ignored a click made before rearming, and reported the next target click armed after completion.
- PR 4 passed 85 platform-neutral tests, 14 Windows tests, a warning-free Release build, repository boundary checks, and the complete offline catalog/build/inspect/export/validate/consume/delete flow. Deterministic packaging and final privacy scans follow this finalized document.
