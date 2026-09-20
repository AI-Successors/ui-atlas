# Task 4: MCP and end-to-end verification implementation

Status: proposed implementation plan only. Governing [requirements](task-4-mcp-verification.requirements.md). Shared field meanings: [Task 1](task-1-mapper.implementation.md).

## Minimal host

Create only one new runtime project: `src/UiAtlas.Core.Mcp`, a local Windows .NET executable using stdio and a pinned MCP library. Reference existing Contracts, Storage/Reader, and Recording.Windows projects. Keep stdout exclusively for protocol traffic; diagnostics go to stderr/local evidence. No HTTP listener, service installation, or generic protocol implementation.

The existing [UiaWorkerClient](../../../src/UiAtlas.Core.Recording.Windows/UiaWorkerClient.cs) relaunches the current entry executable. Dispatch [UiaWorkerHost.Command](../../../src/UiAtlas.Core.Recording.Windows/UiaWorkerHost.cs) before MCP startup so isolated UIA collection works in this host too. Verify this path in the published executable, not only in CLI tests.

Expose four tools with the following responsibilities; use the shared records without a second table schema:

| Tool | Behavior |
| --- | --- |
| list_mapped_grids | Read local map definitions, optionally scoped by map ID; include qualification and reason. No scrolling or automatic acquisition. |
| start_grid_read | Accept map/grid identity, bounded limits, and a caller request ID; return an acquisition ID. Obtain the local confirmation from Task 1, bind fresh, then invoke Tasks 2/3. No caller-supplied arbitrary file paths, coordinates, or actions. |
| get_grid_read | Return lifecycle/stage/progress; on completion include the bounded result. Never restart work. |
| cancel_grid_read | Signal cancellation for that ID and return its observed state. Final restoration/termination outcome is available through get. |

Use an in-memory registry: one active operation, at most four completed results retained for ten minutes. Within that retention window, repeated start with the same request ID and identical arguments returns the same operation; conflicting arguments are rejected. Polling an evicted/expired ID or an ID from a previous host instance returns unknown and never restarts it. After unknown status, the client must not automatically resubmit; a fresh attempt requires a new request ID and approval. Client disconnect or host shutdown cancels active work and follows the same safe-stop rules. Capture evidence remains available for the result lifetime; no durable job scheduler or cross-restart resume.

Task 2's common gate prevents concurrent mapper/server manipulation. The local approval identifies the exact grid and budgets; a model argument is not itself approval. Do not require a recorder session to be open for a fresh read.

## Fixture and independent evaluator

Start this work immediately while other tasks implement. Verify the selected Abacre table and whether a supported window/column configuration genuinely exposes both axes. Freeze one synthetic fixture, its visible presentation, and one question. Candidate question: "Which open orders have Total greater than 20?" Use it only if the relevant columns and synthetic setup support the required offscreen-column demonstration; otherwise agree an equally simple predicate before integration.

Aim for roughly 30-60 records spanning three or four vertical views and at least two horizontal views. Use a supported built-in setup/import or a small documented manual setup; do not build a new fixture app, importer, or database integration. Include blank cells and distinct IDs with otherwise repeated values. Duplicate-label and pathological-overlap checks may use synthetic image tests if the app cannot configure them.

Keep expected IDs/cell strings in a separate fixture manifest. A small evaluator in the existing test/tool structure compares actual MCP result JSON for missing/extra/duplicate IDs, incorrect cells, and the question predicate. The runtime must neither reference nor load this manifest. Mark any temporary stubs as development-only; remove them from the demo path.

## Integration ownership and schedule

Task 4 owns the solution/project wiring, shared dependency updates, final evidence, and integration checklist. Each task owns its named implementation/contract files; coordinate narrow edits to large existing UI files. Task 1 owns the mapper confirmation UI; Task 2 owns all manipulation; Task 3 owns schema/value interpretation.

| Elapsed time | Shared gate |
| --- | --- |
| 0:00-0:20 | Agree minimal records, ownership, fixture, question, and limits. Verify real two-axis feasibility. |
| By 1:00 | One-viewport reader/structure is proven; mapper candidate and MCP shell exist. If reading is unproven, report the blocker and stop expanding features. |
| By 2:30 | Verified two-axis assembly and independent extraction work on the same fixture. |
| By 3:15 | Real MCP call returns the fixture data; run the business question. |
| 3:15-4:00 | Check short budgets, ambiguity, stops, restoration, and mapping regression; fix defects only. |
| 4:00-5:00 | Freeze features, finish required checks, document exact startup/build, and rehearse. Stretch freshness demo only if the core is already sound. |

Update package locks, notices/SBOM and the [repository package allowlist](../../../tools/Test-RepositoryBoundary.ps1) narrowly for the chosen MCP dependency; record new files in provenance. Run locked restore, Release build and tests per [CONTRIBUTING](../../../CONTRIBUTING.md), repository boundary check, and the relevant existing offline/recorder smoke. Run checks once at integration; repeat only affected checks after fixes.

Document measured tool latency/counts, exact binaries, complete/partial/failed cases, restoration, and limitations. Aim to keep the live read/answer segment within one minute; configured ceilings are not performance claims. Failure of horizontal qualification, fixture accuracy, or safety remains an explicit unmet requirement, never a renamed success. No push, publication, or event submission is part of this task.
