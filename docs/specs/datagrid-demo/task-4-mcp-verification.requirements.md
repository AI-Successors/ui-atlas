# Task 4: MCP and end-to-end verification requirements

Status: proposed; no implementation authorization. Parent: [central requirements](requirements.md). Plan: [implementation](task-4-mcp-verification.implementation.md).

## Required outcomes

- **T4.1 (R1, R12):** Add a small public MCP server project that runs locally without the private product. The runtime invokes the shared public acquisition and extraction behavior, not fixture answers or a database export.
- **T4.2 (R3, R12):** List every grid definition in the selected local catalog/map scope, including unqualified mappings and their reasons. Listing is non-scrolling and does not imply the target is currently readable. A later acquisition performs fresh binding.
- **T4.3 (R7, R12):** Support starting one approved grid read, checking its progress/result, and cancelling. Polling and duplicate request handling must not repeat manipulation. Expired or unknown operations return an explicit error; concurrent manipulation is rejected.
- **T4.4 (R9-R12):** Return schema, structured rows, record IDs where present, source context/evidence, timing, terminal data status, and distinct capture/extraction/restoration outcomes. Untrusted application text cannot expand tool actions.
- **T4.5 (R13):** Prepare a small synthetic Abacre fixture with independent expected IDs/values, setup/reset instructions, and a deterministic question predicate. Include a relevant row and a question-relevant column outside the initial viewport. Do not fabricate overflow or assume installed sample data is safe to publish.
- **T4.6 (R13):** Compare actual tool output with independent expected data. Show correct IDs/cells/answer, truthful short-budget behavior, safe stops, and independent restoration. Geometry tests and saved-image replay supplement rather than replace the exact live run.
- **T4.7 (R13):** Supply executable startup commands, client configuration, the supported scope, exact build identity, measured results, a three-minute script, and unresolved limitations. Run the required public checks and preserve unrelated working-tree changes.

## Acceptance and handoff

Own integrated acceptance A1-A8, using focused evidence supplied by Tasks 1-3. A source build, mocked MCP test, or hard-coded response alone does not pass A7.

Deliver the MCP project, fixture/evaluator, concise startup/rehearsal instructions, and a final verification record. A manually changed/added synthetic record appearing in a subsequent read is stretch work after core acceptance.

Exclude HTTP hosting, authentication/RBAC, durable background jobs, queues, multi-user support, general click/type tools, automated data entry, and a new test framework. Do not publish, push, or submit without a separate user instruction.
