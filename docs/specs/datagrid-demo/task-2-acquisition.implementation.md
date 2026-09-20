# Task 2: live capture and stitching implementation

Status: proposed implementation plan only. Governing [requirements](task-2-acquisition.requirements.md). Shared field meanings: [Task 1](task-1-mapper.implementation.md).

## Placement and reuse

Put a small acquisition coordinator and grid traversal/registration helpers in the existing Recording.Windows project. Add plain capture records to Contracts. No new service, process broker, persistent job system, or generic driver registry.

Reuse [WindowCatalog](../../../src/UiAtlas.Core.Recording.Windows/WindowCatalog.cs), [LiveObservationCapture](../../../src/UiAtlas.Core.Recording.Windows/LiveObservationCapture.cs), and the bounded [UIA worker](../../../src/UiAtlas.Core.Recording.Windows/UiaWorkerClient.cs). Review [input monitoring](../../../src/UiAtlas.Core.Recording.Windows/SafeSyntheticInput.cs) before reuse: grid movements must be tagged consistently so our own input does not look like takeover. The existing wheel helper is not already a complete safe traversal implementation.

Use one non-queued operation gate shared by mapper and MCP, including across their processes: a small per-user/session OS semaphore is enough. Return busy for a second operation. Hold it through restoration; no scheduling system.

## Bounded algorithm

1. Bind fresh host ancestry and process instance, refresh geometry, select scroll controls by axis, and obtain Task 3's readable viewport description. Remember the original scroll state with evidence. Obtain explicit approval before input; revalidate after approval because the user may have moved the application.
2. Establish top/left using bounded observed movements, recording these movements too. In mapping-survey mode, survey the first row band horizontally, join overlapping headers/columns, and establish the full ordered schema with Task 3. Persisting that schema belongs to Task 1. In fresh-read mode, verify it against the saved schema.
3. Traverse horizontal views for each row band, return to a verified left boundary, then advance vertically with overlap. Reuse the already accepted first band. Keep the same vertical position during a horizontal sweep; check the return alignment before the next band.
4. Immediately before every input, check foreground ownership and that the intended point/control still belongs to the bound scroll scope. Lost focus during traversal is a stop, not permission to take it back. After movement, capture clean pixels without mapper overlays; revalidate target, header/schema, geometry, and observed displacement. Use row spacing, distinctive overlapping content, and actual motion together. Exclude fixed headers from body registration and retain the header once. Do not register on recurring grid lines alone.
5. If a join is ambiguous, stop extending that connected region and return an explicit partial capture. One smaller-step retry is allowed only when a return to the last verified position can itself be proved; otherwise stop. Never guess the most attractive alignment.
6. Stop on a proven terminal boundary or a limit. No-change without corroborating boundary evidence is lack of progress, not proof of the end. Material layout/data changes invalidate affected coverage; schema visibility changes caused by horizontal scrolling are expected.
7. Restore before lengthy transcription while the target remains safe and the user has not taken over. Verify the restored anchor/position. Report failure or safe skip; reverse wheel count alone is not verification. Task 3 can then read the accepted immutable capture without holding input ownership.

## Limits and retained evidence

Initial demo defaults: at most 60 seconds for bind/probe/capture/restoration after approval, 32 movements, 24 tiles, and 64 MiB of retained PNGs. Count boundary surveys, returns, retries, and restoration. Reserve the last five seconds and sufficient movement allowance for the qualified fixture's restoration; do not spend that reserve extending coverage. Task 3 has a separate bounded extraction allowance. These are proposed ceilings to tune from rehearsal, not measured performance claims.

Use one local acquisition folder with original PNGs and one compact JSON manifest containing source identity, capture times, schema revision, movement receipts, tile hashes/placements, join decisions, gaps, and restoration. No replay UI or event database. Keep synthetic evidence outside tracked source by default. Task 4 owns result lifetime; evidence references must remain valid for that lifetime.

Focused checks: distinctive valid overlap, repeated/blank ambiguity, short terminal movement, cross-axis consistency, budget termination, tagged input/takeover, and safe restoration. Target: primitive+viewport handoff by 1:00, both-axis verified assembly by 2:30, full service handoff by 3:00. If joins remain unproven, report partial/blocker rather than weakening validation.
