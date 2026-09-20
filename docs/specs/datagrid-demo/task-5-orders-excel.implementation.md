# App table extraction to Excel — implementation

Requirements: [requirements](task-5-orders-excel.requirements.md). Status: implemented and attended end-to-end acceptance passed on 2026-09-20 UTC.

## Design

Keep `list_apps` and add read-only `list_app_grids`. A server-generated grid reference joins the app instance, map and saved grid without exposing map IDs as caller inputs. Live binding is rechecked on read.

Extend the existing one-operation registry with a read mode and structured dataset retention. Read polling returns metadata plus bounded row pages. Preserve the existing visual-exploration tools for callers explicitly requesting images; all work shares one active-operation limit.

Reuse `GridImageExplorer` with `FullTable`, exact selected HWND binding and the existing approval/capture/restoration controls. Following restoration, derive column and row regions from fresh original-resolution evidence and run a separate Azure table reader. Reuse the current Azure configuration/credential store and Responses transport conventions. Validate every expected row/column identity before accepting literal text. Image capture does not itself qualify transcription.

Structured reads have a separate bounded traversal profile: 120 seconds, 192 native line movements and 96 screenshots including preparation. Group up to eight line movements using previously proven pixel displacement, with safety checks on every movement. Scrollbar values can describe selection rather than viewport position; they never establish row counts or pixel displacement. Require unique overlapping pixels for every join and an actual edge probe. Preserve the smaller visual-only profile. When thumb restoration is unsupported, reverse recorded line movements within the restoration reserve and require both original scrollbar state and original pixels before reporting success.

Exclude a proven narrow blank selector gutter from business columns only when the next fresh header matches the reviewed first column. Selection highlighting is excluded from registration comparison, not edited out of evidence. A bounded stationary footer is excluded from comparison; a short footer is trimmed from body geometry only when repeated row rules establish the pitch and the remaining strip contains no text. This keeps static chrome out of stitched rows without accepting changed data or ambiguous overlaps.

Use a small local XLSX exporter with no Excel desktop dependency. Export the retained dataset, resolve Downloads through Windows Known Folders, reserve a non-colliding filename, write atomically and verify the serialized workbook before reporting success. Keep source text literal and include a compact extraction-details sheet. The server implementation is portable within the Windows product and does not depend on Codex's artifact runtime; the artifact runtime is used for independent workbook QA.

Install the workflow skill with automatic discovery enabled and maintain its source in this repository. The skill invokes MCP tools only for this workflow and has explicit no-Computer-Use fallback behavior.

## Verification plan

- Unit/contract: exact live app/grid identity, missing or stale map/grid, duplicate request IDs, cancellation, pagination, Azure malformed/duplicate/missing cells, partial coverage, literal spreadsheet values, filename collision and workbook roundtrip.
- Native/UI: approval is required, full-table/Azure scope is visible, HUD remains responsive through capture and extraction, restoration stays separate.
- Protocol: test the real executable and updated tool schema. Live rehearsal through stdio, with actual local approval, uses saved settings and exports only the returned dataset.
- Workbook: independently import and inspect the final XLSX, compare all serialized cells against the read receipt, render representative rows and extraction details.

## Execution evidence

Build 0.4.6 is published at `artifacts/mcp-orders-excel-046/UiAtlas.Core.Mcp.exe` and registered through `codex mcp add ui-atlas-public-grid`. Its MCP assembly SHA-256 is `EC56B7A48885113FBE784F9D321F4C9C606AEABB6BB79B9CCB198E4B273AE2F0`. Publication uses a new directory because an existing Codex connection holds the preceding binary open. Existing connections must reload their tool catalog; the attended client launches the exact new executable directly through MCP stdio. The workflow skill is installed in the user's Codex skills directory with source in `.agents/skills/ui-atlas-grid-export`.

The user requested that the HUD never expire. Both visual exploration and structured reads now await approval without a deadline, while caller cancellation and host disconnect still close the HUD. Identity and geometry are revalidated after approval. A five-second visible countdown allows the operator to settle before capture and takeover monitoring begin. The rehearsal client applies its six-minute watchdog only after active work starts. Capture and Azure processing retain their separate time limits. During Azure processing the HUD says capture is finished and desktop use is permitted.

Verification:

- Final 0.4.6 regression: all 518 Windows tests pass, and all three native CLI grid exploration/restoration tests pass when run without an active live capture. Release solution build has zero warnings and errors. Repository boundary checks pass. The earlier foreground-sensitive failures below are superseded by these passing isolated runs.
- Release build succeeds. Real executable protocol test advertises eight tools and rejects unknown IDs without capture.
- Initial focused protocol, read/export and Azure tests: 27 passed. After the approval correction, focused read/export, HUD and protocol tests: 13 passed; subsequent HUD/export run: 11 passed. Azure cell transport and export tests: 26 passed, including fixed keys, literal values, duplicate headers, blank/unreadable distinctions, cancellation, invalid response rejection, paging, partial export opt-in and collision handling.
- Full Windows regression run: 507 passed, one foreground assertion failed while an attended live capture was also running. All six HUD tests subsequently passed in isolation. Three broader CLI native-grid tests failed at acquiring foreground before exercising traversal; four settings/repository tests passed. These foreground-sensitive failures are disclosed, not counted as passing.
- Final app/grid reference, operation registry and real protocol checks: 19 passed. Changed saved definitions and references from another app fail before binding. Repository boundary checks and `git diff --check` pass. Skill validation passes. HUD rendering inspected; capture and Azure phases are separate.
- Independent artifact-tool import and rendering of the synthetic export compared every cell and all headers successfully. Evidence is under `artifacts/orders-workbook-fixture-qa`; this is test data, not the requested Orders delivery. The renderer visually coerces a leading-zero string, while its imported value and the actual XLSX inline-string value retain the zeros; worksheet XML roundtrip is authoritative for stored values.

Attended runs through real MCP stdio, with no Computer Use calls:

1. `artifacts/orders-excel-live-01`: local approval occurred, but concurrent native tests triggered human takeover before any captures or moves. No data or export.
2. `artifacts/orders-excel-live-02`: approval expired in the older build. No capture or export. This prompted removal of approval expiry.
3. `artifacts/orders-excel-live-03`: approved; stopped on human takeover after two captures and two horizontal movements. Neither axis was proven complete; restoration was safely skipped. No accepted image, extracted rows or export. The input monitor reacts to untagged mouse or keyboard events; the receipt does not identify the particular event.
4. `artifacts/orders-excel-live-04`: 0.4.1 remained AwaitingApproval beyond four minutes, proving removal of the former two-minute expiry. After approval, immediate human takeover stopped capture before any images or moves. This prompted the five-second settling countdown.
5. `artifacts/orders-excel-live-05`: 0.4.2 completed horizontal coverage but exhausted the old image budget before useful vertical traversal (23 captures, 21 movements). Restoration succeeded; schema extraction rejected the narrow blank row-selector gutter. No export.
6. `artifacts/orders-excel-live-06`: 0.4.3 made 44 movements and 20 captures. A later vertical join rejected the selection repaint. Restoration also failed because the first reverse native line movement changed selection position by more than one; the old restoration chain assumed position deltas represented viewport movement. These failures motivated the bounded reverse-history and pixel-verification correction.
7. `artifacts/orders-excel-live-07`: 0.4.4 made 73 movements and 25 captures; restoration succeeded. A later vertical join still failed because fixed footer pixels were compared as scrolling content. Replay of the immutable before/after tiles proved the 168-pixel row displacement. Comparison and body-geometry fixes pass 28 focused tests, including the original failed tiles, changed-data rejection and blank-versus-text footer cases.
8. `artifacts/orders-excel-live-08`: 0.4.5 completed actual full-table traversal: 32 retained captures, 86 recorded movements, all four boundaries and both axes proven, continuous coverage without gaps, and original position plus pixels restored. The resulting 2374×793 image contains 37 rows and 11 business columns. Azure header reading succeeded, but the first 64-image cell batch received HTTP 400. A two-pixel terminal rule was also misclassified as a partial row. No dataset/export was delivered. The next build uses 32-image batches and treats the shared one/two-pixel terminal rule as a border, while retaining partial status for clipped rows. Focused extraction, Azure and export tests: 59 passed.
9. `artifacts/orders-excel-live-09`: 0.4.6 passed the complete attended workflow. First application call was `list_apps`, followed by `list_app_grids`, `start_grid_read`, polling the same acquisition with `get_grid_read`, returned image retrieval, and `export_grid_to_excel`. The operator approved locally; the five-second countdown preceded capture. The identical start retry retained the same acquisition. No Computer Use calls or external desktop automation were used.

## Final acceptance

- Dataset `649c357278a1435c87f3374aa9f74d4a`: **Complete**, 37 rows and 11 columns, with no extraction reasons or unreadable cells. Saved Azure GPT-5.6 Sol settings were reused; 32-image batches succeeded. Capture took 6.23 seconds and Azure extraction 82.98 seconds, excluding approval and settling.
- Column coverage: complete, both horizontal boundaries proven. Row coverage: complete, both vertical boundaries proven. Coverage is continuous with no gaps. This describes the currently reachable Orders table under its existing filters, not hidden or backing records.
- Restoration: **Succeeded**, with original position and original pixels independently verified. The receipt records 32 retained screenshots and 86 movements, including traversal and restoration.
- The returned 2374×793 PNG has SHA-256 `d9771b2ad91fea72bcb41076c23ed74e3e350e37b9698b5ae1bd9046dab6112a`. Fresh rows and headers were visually inspected against the returned data. The source PNG and exact MCP transcript are retained under the ignored run directory above.
- The product exported `Orders-20260919-191420.xlsx` to the Windows Downloads known folder. SHA-256: `e5dac60f878eccda59a3a01cf9ba151d069300ad579e2eb5a523cba3350fbb19`. Product XML roundtrip verification passed. Independent artifact-tool import matched every one of the 407 cell values and all 11 headers; both Orders and Extraction details worksheets were rendered and inspected. QA receipt and renders are in the same run directory.
- Codex registration now points to 0.4.6. An already-connected older host still requires MCP catalog reload before the new application/read/export tools appear in that connection. This acceptance used the exact registered executable through real MCP stdio, not a substitute UI controller.
