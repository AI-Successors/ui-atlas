# Human-readable map export PR rollout

Status: implemented and qualified on 2026-08-11.

## Authority

The required default JSON shape is human-facing rather than a storage-table dump:

```text
rawDataStreams.windows[].variants[].controls[]
rawWorld.windows[].variants[].controls[]
semanticWorld.windows[].variants[].controls[]
```

Every layer retains stable IDs, parent/owner relationships, and explicit source ID arrays to the preceding world. The existing v5 compatibility artifact remains available as `format=ui-atlas_flat`; canonical SQLite remains available as `format=sqlite`.

## PR sequence

### PR 1 - Human map contract

- Add `ui-atlas.map.json/1` and its schema.
- Group observation-specific native windows into Raw Data Streams windows and use captured observations as variants.
- Use durable Raw World surfaces as windows and observed states/frames as variants.
- Use semantic windows directly; represent popup families as windows and semantic popup surfaces as their variants.
- Nest the controls observed in each variant and convert property arrays into readable named objects.

### PR 2 - Explicit lineage

- Preserve source window IDs on windows.
- Preserve source window and source variant IDs on variants.
- Preserve source control IDs on controls, including deterministic Raw Data Streams matching for Raw World controls.
- Retain owner window IDs and parent control IDs.

### PR 3 - Export grammar

- Make `export map <id>` equivalent to `format=json`.
- Route `format=ui-atlas_flat` to the existing v5 `uikg/vnext` publisher.
- Route `format=sqlite` to a validated lossless SQLite publication.
- Detect all formats during validation and recoverably delete each artifact with its checksum.

### PR 4 - Qualification and release

- Exercise all three formats in the isolated offline smoke flow.
- Characterize deterministic JSON and SQLite publication in tests.
- Inspect the existing private Word map without retaining its contents in the repository.
- Run tests, Release build, boundary/privacy gates, deterministic packaging, compliance, and extracted-package smoke.

## Execution result

- The existing 3,443-node Word map exported as Raw Data Streams 2 windows/19 variants, Raw World 8 windows/19 variants, and Semantic World 3 windows/19 variants. Every variant contained a controls array and the document validated with its checksum.
- The synthetic regressions prove byte-deterministic human JSON, required nested collections, popup-family variants, lineage presence, and lossless SQLite publication.
- The platform-neutral suite passed 87 tests and the Windows suite passed 14 tests. The full Release build, boundary gate, offline three-format lifecycle, privacy scans, and package gates complete this rollout.

No private recording, screenshot, label dump, map ID, or machine-local path is retained in the repository or package.
