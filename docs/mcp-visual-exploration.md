# Local MCP visual exploration

The optional Windows stdio host starts with running applications, using the same
`WindowCatalog` discovery as UI Mapper. `list_apps` returns a session-scoped app ID,
window title, process, map count and saved-grid count. It matches saved map session
process identities case-insensitively, including maps with no saved grids. Windows
from separate instances remain separate choices. Listing does not open the HUD,
capture pixels or scroll.

`show_app_grids` accepts that app ID, returns named grids and reviewed columns from
the matching `maps/<map-id>.session.json` files, and opens the HUD. Callers do not
need a map ID. The operator selects one table when several are saved, then approves
horizontal exploration of the visible row band. Unavailable tables remain visible
in the picker with a reason and cannot be approved. A single table is preselected,
but still requires an explicit approval click. Missing maps and empty grid lists
return `no_map` or `no_saved_grids` without starting an operation. Catalog read
failures are reported separately, never silently treated as an absent map.

The host uses the map database to check identity. Older curation-file grid definitions are not
silently migrated. Saved image paths are not read by the MCP tools.

## Build and configure

From the repository root, using the pinned SDK:

```text
dotnet restore UiAtlas.Core.slnx --locked-mode
dotnet build UiAtlas.Core.slnx -c Release --no-restore -p:StopRunningUiAtlasProcesses=false
dotnet publish src/UiAtlas.Core.Mcp -c Release --no-build --no-restore -o artifacts/mcp-visual
```

Add a local STDIO server in Codex Settings, or run `codex mcp add ui-atlas-public-grid -- <absolute-executable-path>`.
The executable is `artifacts/mcp-visual/UiAtlas.Core.Mcp.exe`. Its default catalog is
the current user's local application-data directory under `UiAtlas/Core`.
An optional `--catalog-root <absolute-local-directory>` selects an isolated catalog.
Restart the server in Codex after configuring it. Keep the complete publish
directory together; this is a framework-dependent .NET 10 Windows desktop app.

## Eight tools

| Tool | Purpose |
| --- | --- |
| `list_apps()` | First call: return running apps with app IDs, map presence and saved-grid counts, plus explicit catalog issues. |
| `list_app_grids(appId)` | Read-only current-UI availability with names, saved columns and opaque grid references. |
| `start_grid_read(appId, gridRef, requestId)` | Open approval for a full reachable table capture and Azure cell read; return an acquisition ID promptly. |
| `get_grid_read(acquisitionId, offset=0, limit=100)` | Poll or page the retained dataset with separate capture, extraction, coverage and restoration. Maximum page size is 200 rows. |
| `export_grid_to_excel(datasetId, allowPartial=false)` | Save and verify retained rows directly as a non-overwriting XLSX in Downloads; partial export requires explicit opt-in. |
| `show_app_grids(appId, requestId)` | Return saved grid names and columns for the selected app and open its HUD for table selection and approval; return an acquisition ID immediately. |
| `get_grid_exploration(acquisitionId, includeImage=false)` | Poll progress or terminal scope, column coverage, row coverage and restoration. Request the actual fresh PNG with `includeImage=true`. |
| `cancel_grid_exploration(acquisitionId)` | Close pending approval or stop the current capture or read; continue polling until finished. |

Version 0.4 adds structured reads and Excel export to the version 0.3 app-based tools.
Restart the MCP connection after replacing the executable so the client reloads
the tool list. App IDs from an earlier host are invalid; call `list_apps` again.

The server resolves the saved process/window/ancestor/host selector only against
the selected live app window. The process ID, start time and owner must still match;
a recycled handle or another instance never becomes an automatic substitute.
After selection, binding uses the exact HWND even when an owned HUD becomes the
window catalog's representative for a legacy app with a hidden owner.
Window movement is supported; changed host size or position
within the window requires reviewing the grid again. A missing or ambiguous
target fails before input. Saved partial schemas and unqualified OCR do not block
image exploration. They remain unqualified and are never promoted by this tool.

Exploration identifies the columns and their layout. It sweeps from the left edge
to the right edge at the current vertical position, using visible rows as a sample.
It stops at the right boundary and restores the starting view. It does not visit
other row bands. Both the mapper's Explore action and the MCP tool use this default.

The result has `scope: "VisibleRowBand"`. `status: "Complete"` means that scope
was captured successfully. `coverage.columnCoverageComplete` and
`coverage.rowCoverageComplete` describe the two dimensions independently. For
example, full-width exploration can complete with 20 visible rows while 36 rows
exist in the table; its row coverage remains incomplete. Top/bottom boundary
flags contain only observed evidence and are not set merely because vertical
traversal was outside the requested scope.

Data extraction uses the separate full-table acquisition path. It traverses both
axes and still requires complete row and column coverage, verified joins and cell
transcription before reporting complete data. A complete exploration image does
not satisfy those checks. The shared image engine can explicitly request
`FullTable` for exceptional visual investigations; the normal MCP tool requests
only `VisibleRowBand`. `start_grid_read` explicitly requests `FullTable`, then reads fixed cells with the saved Azure provider after desktop restoration. Full-table capture is limited to 120 seconds, 192 movements and 96 images, with time and movement reserved for restoration; Azure reading is limited to three minutes, 1,000 rows and 64 columns. Approval waits indefinitely, followed by a five-second settling countdown before capture. Visual-only exploration retains its 60-second/32-movement/24-image profile. Partial or failed results are never described as all data. Current application filters remain in effect.

For an attended end-to-end rehearsal, run `tools/Invoke-GridMcpExplore.ps1` with the published executable, catalog root, output directory, `-AppProcessName ahms -ReadGridName Orders`. It is an MCP stdio client, never a desktop controller. It waits for local human approval, polls to completion, and exports only complete data. The optional [workflow skill](../.agents/skills/ui-atlas-grid-export/SKILL.md) routes natural-language requests through these tools without Computer Use. Do not run native-window tests concurrently with a live read; their focus changes correctly trigger the takeover guard.

The floating capsule at the top of the target monitor names the application, table
and requested scope. Its attached approval card stays open until approval, cancellation or host disconnect; waiting for the operator has no timeout.
Only the operator can approve it; tool arguments cannot grant approval. A fresh
identity and geometry check follows approval. The approved window is foregrounded
and temporarily raised; its previous topmost state is restored without reclaiming
foreground after human takeover. The shared explorer retains target, occlusion,
input-takeover, cancellation, cross-process operation-gate and restoration checks.

After approval the card collapses to a non-activating capsule showing desktop
control, the selected table, elapsed time, capture count and **Stop**. The HUD stays
visible through safe stopping and restoration, then closes when the operation
ends. Capture counts include preparation; they are budget counters, not full-table
coverage. Moving the mouse or typing still takes over, including moving toward
Stop; restoration can be safely skipped after takeover. No input-monitor exemption
is granted to the HUD. The active capsule never overlaps the approved table. If
there is no room at the top of that monitor, exploration fails before scrolling;
move the table lower and request a fresh exploration. The small table symbol on
the approval card is an icon, not a capture taken before approval.

Limits are fixed at 60 seconds, 32 movements and 24 captures including the initial
preparation capture. The coordinator reserves time and movements for restoration.
The same `GridImageExplorer` used by the mapper assembles verified image overlaps.
No cell OCR, database connection, data edits or automatic navigation to another
screen is performed. Complete exploration is not a verified business-data answer.

Only one operation runs per host. Identical request IDs deduplicate while retained;
conflicting reuse fails. Four terminal results are retained for ten minutes.
Unknown, expired and prior-host IDs never restart work. Disconnect cancels active
work, waits for its safe stop, and removes that host's generated evidence.
Only fresh generated output can be returned as an image. PNG responses are capped
at 8 MiB; larger images retain their local evidence and report the output limit.

## Attended acceptance

1. Open the mapped table, finish any recorder operation, and call `list_apps`.
2. Call `show_app_grids` for the selected app ID. Choose a table in the HUD when
   several are saved and approve it. Leave input idle during capture.
3. Poll until finished, then request the image. Confirm timestamps, live source,
   PNG hash, requested scope, per-axis coverage, stop reason and restoration result.
4. Report partial exploration if column coverage could not be established. Missing
   top/bottom boundaries do not make a successful horizontal exploration partial;
   they mean rows are a sample. Never answer a full-table business question without
   complete two-axis acquisition and verified extraction.

Example Codex prompt: "Use only ui-atlas-public-grid. List available apps first.
Then show data grids for Abacre Hotel Management System, wait for my local HUD
selection and approval, poll until finished, and show the returned image if obtained.
Report column coverage, row coverage and restoration separately."
