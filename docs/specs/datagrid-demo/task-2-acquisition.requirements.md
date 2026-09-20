# Task 2: live capture and stitching requirements

Status: proposed; no implementation authorization. Parent: [central requirements](requirements.md). Plan: [implementation](task-2-acquisition.implementation.md).

## Required outcomes

- **T2.1 (R3, R6-R7):** Resolve one selected live table from the mapping and current process/window evidence. Ambiguous, stale, occluded, or unsafe input targets must not receive scrolling.
- **T2.2 (R6-R7):** Keep data bounds distinct from the enclosing scroll-control scope. Identify movement capability by axis. Scrolling requires approval, a readable starting viewport, explicit limits, cancellation, and exclusive ownership of manipulation.
- **T2.3 (R6, R8):** Capture content outside both initial viewport dimensions. Verify the table and observed movement after each bounded step. No sorting, filtering, pagination, or record edits.
- **T2.4 (R8):** Prove compatible overlap before accepting each join. Handle shorter terminal movements. Do not infer image displacement solely from scrollbar percentage, similarity rank, or scroll-command success.
- **T2.5 (R6, R8):** Preserve distinct row occurrences, repeated headers, column order, and partial rows. An ambiguous join must not silently drop or duplicate records or extend claimed coverage.
- **T2.6 (R10):** Retain original images, identity, geometry, movement history, accepted/rejected join evidence, capture interval, and the exact failure stage. Keep verified regions usable when later stages fail.
- **T2.7 (R7, R11):** Stop on takeover/cancellation/loss/change/limits. Restore only when safe and report its verified outcome independently. Never perform surprise restoration after human takeover.

## Acceptance and handoff

Supply evidence for A2 and capture portions of A4-A6. Include a short terminal movement, an ambiguous repetitive overlap, and a target-change/takeover stop. Demonstrate real two-axis traversal on the selected Abacre table; synthetic image checks alone do not qualify it.

Deliver accepted tiles and their placements, row/column coverage, provenance, stage diagnostics, and restoration to Task 3. Do not claim that complete capture proves complete transcription. A full-size combined bitmap is optional; a verified logical assembly is sufficient.

Exclude a general scrolling driver framework, automatic resumption, background monitoring, and universal handling of frozen/merged/variable-height layouts.
