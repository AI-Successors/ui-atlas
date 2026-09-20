Implement Task 1: mapper integration, durable grid definitions, and minimal diagnostics UX.

Read C:/stuff/dev/successors/ui-atlas/docs/specs/datagrid-demo/task-1-mapper.requirements.md and C:/stuff/dev/successors/ui-atlas/docs/specs/datagrid-demo/task-1-mapper.implementation.md.

Own the mapping/schema records in src/UiAtlas.Core.Contracts/DataGridMappingContracts.cs, the small mapper/confirmation UI, narrow CLI/Reader integration, and mapped-grid persistence in the existing Storage/curation mechanism. Treat the proposed record filename as the initial ownership boundary; agree changes promptly with peers.

Publish MappedGridDefinition, GridSchema, and column identity contracts first. A mapped grid is one table with ordered physical columns and qualification, not one map node per business cell. Bypass the existing generic cell detector for this new path while preserving unrelated recorder behavior. Provide a small table/header/body preview, columns, progress, failure stage, coverage, and separate restoration outcome. Supply the shared explicit local confirmation UI usable by the mapper and MCP host.

Task 2 owns all live binding/input/traversal/restoration. Task 3 owns viewport structure interpretation and value reading. Consume their agreed services rather than implementing duplicates. Task 4 owns the MCP server, solution/dependency integration, and final live qualification.

Verify backward-compatible loading, grid-definition round-trip, duplicate label identity, preservation of an existing schema after partial attempts, and relevant mapper regression. Deliver the smallest working mapper flow, not a new editor shell.

Implementation is now authorized by the user; the specifications' earlier "proposed / no implementation authorization" labels describe the preparation phase and do not block this instruction.

Read the applicable AGENTS.md and CONTRIBUTING.md, then the central requirements at C:/stuff/dev/successors/ui-atlas/docs/specs/datagrid-demo/requirements.md and the task pair below. The central requirements govern behavior; implementation details may be simplified without weakening acceptance. The specs are currently uncommitted in that public checkout and may be absent from your isolated worktree. Read the absolute paths above; resolve their source links against that checkout when needed.

Use only the public UI Atlas source and newly authored code. Do not inspect or use sibling private repositories, private runtimes, recordings, tests, credentials, or implementation history. One Abacre table, both scrolling axes, synthetic records, and one business question are the scope. Keep the solution minimal: no generic framework, new dashboard, remote service, write-back, or extra application.

The shared five-hour window starts 2026-09-19 18:07:36 UTC. Freeze features by 22:07:36 UTC and deliver the final integration/handoff by 23:07:36 UTC; finish earlier when done. This is one wall-clock budget for all four tasks. Surface a failed one-viewport or two-axis gate immediately; do not rename a partial result as complete.

Work only in your assigned public worktree. Preserve existing changes. Do not modify the original checkout or another task's worktree. Do not commit, push, create PRs, publish, or submit to the event. Do not create additional chats or agents. Record your new files in your worktree's provenance ledger.

Four peer tasks are running; their IDs will follow in a coordination message. Within the first 20 minutes, exchange exact record/service signatures and owned paths with Task 4 and affected peers. Keep ownership narrow and proceed on independent work while dependencies are prepared. Read/copy an agreed public peer handoff into your own worktree when needed; never overwrite a peer's work. Task 4 integrates completed handoffs into its worktree.

Coordinate all live Abacre checks through Task 4 so only one task interacts with the desktop at a time. Implementation authorization does not bypass the specification's explicit approval for a bounded scrolling operation. No live data edits or synthetic data insertion without the user's specific authorization.

Report early contract decisions and blockers to Task 4. At handoff provide your absolute worktree path, exact changed/new file manifest, public dependency files borrowed from peers, interface signatures, focused check results, and remaining live-qualification gaps. Distinguish tested code from a fresh live proof.
