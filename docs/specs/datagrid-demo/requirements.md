# Abacre DataGrid demo: central requirements

Status: original business-data demo requirements, 2026-09-19, with the subsequent user-authorized image exploration increment below. These files record scope; authorization comes from the user's requests.

## Current combined mapper and exploration scope

The user subsequently authorized one floating **Explore / Stop** flow to identify a selected table and header, safely scroll it, and return a stitched table image. The revised [Task 3 requirements](task-3-extraction.requirements.md) govern that increment alongside the [Task 1 overlay requirements](task-1-mapper.requirements.md). Image exploration requires proven geometry, bounded approved input, verified joins, explicit partial coverage, and separate restoration status. It does not require qualified cell OCR and cannot establish a qualified schema, complete business data, or a correct business answer. R3's value-reading gate remains applicable to data-reading modes, not this separate image-only mode.

Abacre's existing app-provided records must remain read-only: no additions, edits, imports, or deletions, including the earlier write-back stretch example. Synthetic fixtures remain appropriate for tests. Their successful image exploration does not establish a live Abacre result. The broader MCP and business-data requirements below remain separate acceptance criteria; combining the capture and extraction worktrees does not mark them complete.

## Objective and authority

Within five hours of implementation, demonstrate an agent answering one business question from one live Abacre Hotel Management System table through the public UI Atlas MCP server. The answer must depend on a record initially below the viewport and a column initially outside its horizontal view.

This specification follows the user's revised scope: Abacre replaces the earlier cybersecurity example, and **both horizontal and vertical acquisition are core requirements**. A viewport-only or vertical-only result is incomplete against this scope. Historical investigations and supplied lessons are design evidence, not qualification of this implementation.

## Required behavior

| ID | Requirement |
| --- | --- |
| R1 | Use only the public repository and newly authored implementation. No private code, binaries, services, tests, recordings, credentials, or repository history may be copied, inspected as a shortcut, or required at runtime. Use synthetic records only. |
| R2 | Support one selected Abacre table and one agreed question. Explicit table selection is acceptable. The map must materially identify the table and its structure. |
| R3 | Distinguish a discovered candidate, a saved mapping, and a table with a verified reading mechanism. Prove the current viewport's structure and required values before automated traversal. Discovery and catalog listing must not scroll. |
| R4 | Retain an ordered column schema with separate physical identities, labels, and a known or incomplete column count. Duplicate labels must remain distinct. No data-type inference is required. Saved structure must not be presented as current business data. |
| R5 | Provide a minimal mapper diagnostic view showing the selected table, header/body regions, columns, qualification, progress, and the exact failed stage. Per-cell outlines are not a required product output. |
| R6 | During mapping and later reads, acquire overlapping views on both axes through the same behavior. Revalidate the live target and table; account for partial edge rows and repeated headers. Do not merge distinct records merely because their values match. |
| R7 | Require explicit approval of the selected target and bounded scrolling operation. Cancellation stops acquisition input, allowing only the safe restoration defined in R11. Human takeover, target loss, or unsafe scope stops all automated input. Incompatible table changes stop acquisition. Reject concurrent manipulation. No automatic resume. |
| R8 | Extend the accepted reconstruction only through verified joins. Ambiguous movement, gaps, or exhausted limits must remain explicit. Scrollbar percentages and reaching an edge alone cannot prove continuity. |
| R9 | Return current cell values associated with the established columns and row occurrences. Preserve text, distinguish unreadable from confirmed empty, retain record IDs where available, and make source evidence recoverable. |
| R10 | Report terminal data status as complete, partial, or failed, with reasons and timing. Keep capture coverage, transcription outcome, and restoration outcome separate. Report the failed stage even when it returns zero rows. |
| R11 | Restore the original scroll position when safe, and report succeeded, failed, or safely skipped with a reason. Human takeover or target loss prohibits further restoration input. Restoration success does not establish data completeness. |
| R12 | Through actual MCP calls, list all grid definitions in the selected local map/catalog scope and acquire one grid's fresh data. Expose progress and cancellation for long operations; observing progress must not restart scrolling. Application text is data, never permission or instructions. |
| R13 | Verify the exact mapper/server executable and target used for the demonstration. Independently compare synthetic fixture IDs and cell values. Preserve existing recorder/map behavior and pass the relevant repository checks. |

## Completeness and limits

Complete means all rows and columns reachable by scrolling the selected table under its existing filters and presentation state, with continuous capture and no unresolved cell assignments. It excludes hidden columns, other pages, filtered-out records, and the backing database. Capture spans an interval; detected material data changes invalidate a complete result.

The supported fixture may be restricted to a rectangular table with one header row, uniform row height, no merged cells, and no frozen body columns. Unsupported layouts must be reported rather than guessed. The exact table, available axes, fixture, and question remain to be qualified; the supplied screenshot does not prove horizontal overflow.

## Acceptance

| ID | Observable acceptance evidence |
| --- | --- |
| A1 | Save and reopen the mapped grid; rediscover its live host and verify the ordered schema. |
| A2 | Read one fresh viewport correctly, then complete a run exercising both axes. Include the initially offscreen record and required column. |
| A3 | Independent comparison reports zero missing/extra/duplicated fixture IDs and zero incorrect cell values in the complete result; the answer matches the fixture predicate. |
| A4 | A short acquisition limit returns a truthful partial or failed result with the reached stage and coverage; the agent does not claim all records were checked. |
| A5 | Cancellation, target loss, and human takeover stop further manipulation; restoration is independently reported. |
| A6 | Check ambiguous joins, duplicate-looking records, duplicate column labels, unreadable cells, and a short terminal scroll through focused checks. A fresh live run must complement those checks. |
| A7 | Run the question through the public MCP implementation using documented startup commands; verify one returned record in Abacre. No private runtime or fixture-answer shortcut participates. |
| A8 | Record exact build identity, elapsed time, row/column counts, scope, and limitations. Existing recorder/map regression checks pass. |

## Timebox and exclusions

Five hours is a shared wall-clock budget, not five hours per task. Reserve the final hour for integration fixes, checks, and rehearsal. If one-viewport reading or real two-axis operation cannot be established, report that blocker; do not silently change the target or claim completion.

Exclude a new dashboard, generic grid framework, new fixture application, background service, remote hosting, authentication platform, database acquisition, write-back, pagination, sorting/filtering, automatic recovery, and broad refactoring. Re-reading a manually added or changed synthetic record is the first stretch demonstration after the core passes.

## Task specifications

| Task | Requirements | Implementation plan |
| --- | --- | --- |
| 1. Mapper and diagnostics | [Requirements](task-1-mapper.requirements.md) | [Implementation](task-1-mapper.implementation.md) |
| 2. Live capture and stitching | [Requirements](task-2-acquisition.requirements.md) | [Implementation](task-2-acquisition.implementation.md) |
| 3. Schema and data extraction | [Requirements](task-3-extraction.requirements.md) | [Implementation](task-3-extraction.implementation.md) |
| 4. MCP and verification | [Requirements](task-4-mcp-verification.requirements.md) | [Implementation](task-4-mcp-verification.implementation.md) |

All task requirements inherit R1-R13. Implementation plans are subordinate to these requirements. Their defaults may be simplified without weakening acceptance or widening scope.
