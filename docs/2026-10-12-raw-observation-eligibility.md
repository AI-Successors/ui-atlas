# Raw observation eligibility and hidden MSAA descendants

Follow-up to PR #1 and the [Raw Data Streams rollout](2026-10-11-raw-data-streams-parity-pr-rollout.md).

**Correction:** the interrupted-frame exclusion described below was too broad.
The [MSAA coverage and quality follow-up](2026-10-12-word-msaa-coverage-and-quality.md)
keeps foreground observations with real provider evidence, including Colors 11,
and withholds only unsafe image/geometry pairing. The execution results below
describe the earlier build, not the corrected acceptance policy.

## Evidence and root cause

Reviewed the 199.6-second `20261012-0156-08.8869690.mp4`, its narration,
the six supplied screenshots, and the original `live-word.mlrec` records.
Private media and recording files stay outside Git.

| Video / report | Recorded facts | Defect |
| --- | --- | --- |
| 00:43–01:01, Main 6–11 | Font is ahead of Word in recorded native Z order; Colors is ahead of both in 11. | The strip lists every captured HWND. Masking the covering dialog leaves an irrelevant background-window card. |
| Main 3; Font 7, 10–11 | Main 3 has 211 UIA Raw records; Font 7 has 45. Both acquisitions are interrupted/changed. Font 10–11 have three UIA timeouts, but 236 MSAA records each. | The stability gate correctly withholds overlays, but the default strip still offers these as ordinary observations. This is not always missing automation. |
| 01:08–01:49, tooltip 4 | `uia.raw/0x55211A:003` contains five records: pane, tooltip, group, and two text records with the exact tooltip strings and bounds. | The displayed Raw attempt is directly reported, not fused from Word. Legacy Control/Content attempts also contain 203/180 entries and lack verified ownership; those attempts must not be described as verified tooltip controls. |
| 01:55–02:24, Font 6 | Seven MSAA list items named `(none)`, `Blinking Background`, `Las Vegas Lights`, `Marching Black Ants`, `Marching Red Ants`, `Shimmer`, and `Sparkle Text` have `IsOffscreen=false`, but their parent `9c13d4.msaa.r0.4c` is hidden. | The raw canvas checks only the child's flag. Hidden descendants draw over the real Font list. Graph visibility also resolves ancestors across flattened provider attempts, allowing repeated runtime IDs to collide. |
| 02:48–03:16, Font/Colors 11 | Colors and Font are separate HWND observations. Both were captured during a changed acquisition. | Font should be excluded as a background window; neither interrupted observation should masquerade as a complete screenshot/automation pair. |

## Goals

- Default Raw Data Streams cards show the frontmost recorded normal/dialog window
  for each process, with its captured owned menus preserved.
- Exclude interrupted, unstable, unavailable, and empty normal/dialog observations
  from that default strip. A successful MSAA response remains usable when UIA times
  out; UIA is not the only native data stream.
- Keep borderless surfaces with captured pixels inspectable even when their provider
  returned no controls. Do not invent controls for them.
- Provide an explicit **Show excluded** diagnostic switch and reasons. **Frame
  streams** continues to expose every frame, provider attempt, timeout, and record.
- Respect reported hidden ancestors within the same attempt on the raw canvas.
  **Show offscreen** deliberately reveals those records for diagnosis.
- Apply the fixes to existing saved maps without modifying their source bundles.

## Non-goals

No scene reconstruction, geometry repair, cross-provider fusion, inferred controls,
or historical screenshot inheritance. No deletion of raw records, blanket removal
of MSAA, automatic dialog-tab traversal, capture scheduling change, or grid changes.
Native Z order means frontmost among the recorded same-process windows, not proof
of global desktop focus. Unknown/missing evidence must be explained, not guessed.

## Rollout tasks

1. Add a pure eligibility policy using native window metadata and sealed frame facts.
2. Enable default eligibility filtering; expose excluded cards and exclusion reasons,
   including a useful empty state when all observations are excluded.
3. Resolve raw canvas visibility from each provider attempt's own ancestry, including
   existing maps. Apply the same separation to newly built graph visibility metadata.
4. Prefer a provider attempt with visible, usable records to an empty/root-only one;
   keep all attempts selectable with their original provenance.
5. Test normal/dialog ordering, unrelated processes, owned menus, interrupted and
   empty evidence, hidden ancestors, cycles, and repeated IDs across provider attempts.
6. Replay the retained Word map, verify its exact card sets and Font rectangles,
   run the relevant test suites and normal Visual Studio build, and update PR #1.

## Acceptance and rollback

For the retained merged map, default Main cards should be 1, 2, 4, 5 and resumed 1;
Font cards should be 6, 8, 9. Changed Colors 11 remains available through Show excluded
and Frame streams, with its 18 UIA Raw records preserved. Tooltip 4 retains the two
directly reported text rectangles; the seven hidden MSAA animation items disappear
from Font 6's default canvas. Source counts and saved recording bytes remain intact.

Revert this follow-up to restore the previous presentation. No schema migration or
source recording rewrite is required. Validation results are appended after execution.

## Execution results

- Focused eligibility/menu/parity run: **30/30 passed**, including seven new
  eligibility and visibility cases. Clean task snapshot: **478/478 Core tests
  passed**, excluding the seven native desktop cases with separately documented
  baseline qualification in the parent rollout.
- Full clean Release solution build: **zero warnings and errors**. The snapshot
  contains only the task changes plus the installed SDK feature-band selection.
- Rebuilt the retained 11-frame Word bundle with the clean Release binary, saved
  and reopened it, and passed strict graph validation. All **11,619 raw records**
  match the original provider records field for field: zero missing or extra
  records, zero overlapping acquisitions. The existing merged viewer still reports
  **12,492 entries** including its resumed session.
- Live saved-map inspection confirms Main cards **1, 2, 4, 5, resumed 1** and Font
  cards **6, 8, 9**. Main/frame 2 still includes its captured font menu. Font/frame 6
  Structure Overlay now aligns without the hidden animation-list rectangles.
- **Show excluded** restores all 12 Main cards, labels the excluded ones, and
  exposes the exact background-window or interrupted-capture reason in accessibility
  help and tooltips. No source map or bundle was rewritten during this inspection.
- Windows capture tests and the seven native desktop cases were not rerun for this
  presentation/build-metadata follow-up. Existing capture timeout and stability
  limitations remain explicit; no fresh recording or grid export is claimed.
- Repository boundary checks passed. Normal Visual Studio Build produced **UI Atlas
  Core 2.0.81**: ten projects built, four up-to-date, zero failures. The recorder
  and staged viewer both identify implementation commit `efa6e11`. Normal output
  includes the user's existing changes; the clean qualification above excludes them.
