Implement Task 2: the shared bounded live DataGrid capture and stitching engine.

Read C:/stuff/dev/successors/ui-atlas/docs/specs/datagrid-demo/task-2-acquisition.requirements.md and C:/stuff/dev/successors/ui-atlas/docs/specs/datagrid-demo/task-2-acquisition.implementation.md, plus the shared field meanings in C:/stuff/dev/successors/ui-atlas/docs/specs/datagrid-demo/task-1-mapper.implementation.md.

Own src/UiAtlas.Core.Contracts/DataGridAcquisitionContracts.cs and the acquisition, target-binding, scrolling, registration, evidence, and restoration helpers within Recording.Windows. Own any necessary shared UiaWorkerHost/UiaWorkerClient or input-helper edits; coordinate Task 3's native-probe needs rather than both editing those files independently.

Publish AcquisitionRequest, AcquisitionProgress, CapturedGrid, coverage/restoration records, and the coordinator's service signature early. Consume Task 1's mapping/schema records and Task 3's viewport describer. Use one operation gate across mapper/server processes. Check the target immediately before input; stop on human takeover, loss, unsafe scope, or cancellation according to the central restoration rules.

Implement the observe-move-observe-verify loop on both axes with conservative incremental joins, original tiles and a compact manifest. Scroll percentages cannot substitute for captured displacement. Preserve duplicate-looking record occurrences, handle shorter terminal steps, and report ambiguous/gapped/budget-limited capture explicitly. Restore safely before lengthy extraction and verify the restoration separately.

Prove core geometry and stop behavior with focused synthetic checks while awaiting exclusive live access. Do not build a driver framework, scheduler, or assume the existing wheel helper already supplies safe acquisition. Deliver a reusable service for both mapping and MCP, not a recorder-only path.

Implementation is now authorized by the user; the specifications' earlier "proposed / no implementation authorization" labels describe the preparation phase and do not block this instruction.

Read the applicable AGENTS.md and CONTRIBUTING.md, then the central requirements at C:/stuff/dev/successors/ui-atlas/docs/specs/datagrid-demo/requirements.md and the task pair below. The central requirements govern behavior; implementation details may be simplified without weakening acceptance. The specs are currently uncommitted in that public checkout and may be absent from your isolated worktree. Read the absolute paths above; resolve their source links against that checkout when needed.

Use only the public UI Atlas source and newly authored code. Do not inspect or use sibling private repositories, private runtimes, recordings, tests, credentials, or implementation history. One Abacre table, both scrolling axes, synthetic records, and one business question are the scope. Keep the solution minimal: no generic framework, new dashboard, remote service, write-back, or extra application.

The shared five-hour window starts 2026-09-19 18:07:36 UTC. Freeze features by 22:07:36 UTC and deliver the final integration/handoff by 23:07:36 UTC; finish earlier when done. This is one wall-clock budget for all four tasks. Surface a failed one-viewport or two-axis gate immediately; do not rename a partial result as complete.

Work only in your assigned public worktree. Preserve existing changes. Do not modify the original checkout or another task's worktree. Do not commit, push, create PRs, publish, or submit to the event. Do not create additional chats or agents. Record your new files in your worktree's provenance ledger.

Four peer tasks are running; their IDs will follow in a coordination message. Within the first 20 minutes, exchange exact record/service signatures and owned paths with Task 4 and affected peers. Keep ownership narrow and proceed on independent work while dependencies are prepared. Read/copy an agreed public peer handoff into your own worktree when needed; never overwrite a peer's work. Task 4 integrates completed handoffs into its worktree.

Coordinate all live Abacre checks through Task 4 so only one task interacts with the desktop at a time. Implementation authorization does not bypass the specification's explicit approval for a bounded scrolling operation. No live data edits or synthetic data insertion without the user's specific authorization.

Report early contract decisions and blockers to Task 4. At handoff provide your absolute worktree path, exact changed/new file manifest, public dependency files borrowed from peers, interface signatures, focused check results, and remaining live-qualification gaps. Distinguish tested code from a fresh live proof.
