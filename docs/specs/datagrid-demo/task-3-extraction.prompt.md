Implement Task 3: viewport structure/capability assessment and structured cell extraction.

Read C:/stuff/dev/successors/ui-atlas/docs/specs/datagrid-demo/task-3-extraction.requirements.md and C:/stuff/dev/successors/ui-atlas/docs/specs/datagrid-demo/task-3-extraction.implementation.md, plus the shared field meanings in C:/stuff/dev/successors/ui-atlas/docs/specs/datagrid-demo/task-1-mapper.implementation.md.

Own src/UiAtlas.Core.Contracts/DataGridExtractionContracts.cs and the viewport describer/table-reader helpers in Recording.Windows. Task 1 owns persisted mapping/schema definitions; agree those types rather than duplicating them. Task 2 owns captured placements, traversal, and edits to shared UIA worker/input files. Send Task 2 narrowly scoped native-probe requirements or patches for those shared files.

Publish the viewport-description and accepted-capture extraction service signatures early. Briefly probe native container Grid/Table capabilities as well as descendants. Establish physical columns, headers, row geometry, and partial rows before assigning text. Preserve duplicate header identity and the fixed schema across viewport changes.

Qualify one reading mechanism for the selected Abacre table. Reuse local Windows OCR at the lowest appropriate level for opaque content; do not reuse repeated-word removal or failure-as-empty label behavior as business-data semantics. Return literal text, proven empty, or unreadable with source regions. Final visual transcription operates only on accepted capture regions; never repair an uncertain join with plausible text.

Use bounded row batches and validate missing/extra rows, shifted columns, conflicts, and source IDs. Keep acquisition-local row identity separate from record ID. Never read the expected fixture manifest from runtime acquisition code. No new cloud provider, type-inference system, analytics engine, or generic OCR framework.

Provide focused checks and a one-viewport proof as early as possible; accurately report if native/OCR reading remains unqualified. The agent will answer the business question from your structured result.

Implementation is now authorized by the user; the specifications' earlier "proposed / no implementation authorization" labels describe the preparation phase and do not block this instruction.

Read the applicable AGENTS.md and CONTRIBUTING.md, then the central requirements at C:/stuff/dev/successors/ui-atlas/docs/specs/datagrid-demo/requirements.md and the task pair below. The central requirements govern behavior; implementation details may be simplified without weakening acceptance. The specs are currently uncommitted in that public checkout and may be absent from your isolated worktree. Read the absolute paths above; resolve their source links against that checkout when needed.

Use only the public UI Atlas source and newly authored code. Do not inspect or use sibling private repositories, private runtimes, recordings, tests, credentials, or implementation history. One Abacre table, both scrolling axes, synthetic records, and one business question are the scope. Keep the solution minimal: no generic framework, new dashboard, remote service, write-back, or extra application.

The shared five-hour window starts 2026-09-19 18:07:36 UTC. Freeze features by 22:07:36 UTC and deliver the final integration/handoff by 23:07:36 UTC; finish earlier when done. This is one wall-clock budget for all four tasks. Surface a failed one-viewport or two-axis gate immediately; do not rename a partial result as complete.

Work only in your assigned public worktree. Preserve existing changes. Do not modify the original checkout or another task's worktree. Do not commit, push, create PRs, publish, or submit to the event. Do not create additional chats or agents. Record your new files in your worktree's provenance ledger.

Four peer tasks are running; their IDs will follow in a coordination message. Within the first 20 minutes, exchange exact record/service signatures and owned paths with Task 4 and affected peers. Keep ownership narrow and proceed on independent work while dependencies are prepared. Read/copy an agreed public peer handoff into your own worktree when needed; never overwrite a peer's work. Task 4 integrates completed handoffs into its worktree.

Coordinate all live Abacre checks through Task 4 so only one task interacts with the desktop at a time. Implementation authorization does not bypass the specification's explicit approval for a bounded scrolling operation. No live data edits or synthetic data insertion without the user's specific authorization.

Report early contract decisions and blockers to Task 4. At handoff provide your absolute worktree path, exact changed/new file manifest, public dependency files borrowed from peers, interface signatures, focused check results, and remaining live-qualification gaps. Distinguish tested code from a fresh live proof.
