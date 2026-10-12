# Word MSAA coverage and fresh recording qualification

Follow-up to [PR #1 rollout](2026-10-11-raw-data-streams-parity-pr-rollout.md).

## Problems and fixes

1. The retained Font dropdown has 21 MSAA entries, including Theme Fonts and
   Recently Used Fonts, but no All Fonts subtree. The collector required every
   rectangle to fit completely inside the popup and only traversed accepted
   containers. A scrolling container extending past the viewport could therefore
   remove visible descendants. Preserve provider rectangles during tree collection
   and continue traversal independently of geometry acceptance. Keep point-query
   containment checks. Verify the actual improvement with a fresh Word capture.
2. The eligibility follow-up hid Colors frame 11 because the overall acquisition
   was interrupted/changed, despite successful direct provider responses. Keep
   foreground observations with usable native evidence; use timing uncertainty to
   withhold synchronized overlays/control crops, not to hide the observation.
   Font and Main behind Colors remain excluded. Explain timing uncertainty in every
   raw presentation mode, including Window.
3. The fresh finish-map run exposed a second boundary defect: Word reports negative
   rectangle heights for many offscreen MSAA items. Those records correctly belong
   in raw evidence, but the legacy automation rollup was also fed to strict derived
   input validation. Preserve raw payloads and exclude non-bounded rectangles only
   when preparing derived controls. Recorded-input validation accepts raw geometry;
   ordinary derived/live input remains strict. Cover enriched and direct bundle
   builds plus save/reopen in a regression test.

## Goals and non-goals

Capture the main window, Font dropdown, Font dialog, and Colors dialog in a new
manual recording; finish the map, reopen it, audit raw provider preservation, and
inspect the rendered results. Preserve all raw attempts, provider identity, window
identity, original geometry, and separate window pixels. Keep existing grid work.

Do not manufacture rectangles, copy main-window UIA into popup streams, reconstruct
a scene at Raw Data Streams, automatically traverse dialog tabs, or rewrite the
old recording to pretend it contains newly collected data. Provider limitations and
timeouts remain reportable; this is a bounded Word qualification, not a fleet claim.

## Rollout and acceptance

- [ ] Correct foreground-card eligibility; regress interrupted Colors with a
      successful response and preserve the background-window/overlay safeguards.
- [ ] Correct MSAA subtree traversal; verify All Fonts descendants are captured
      from the popup itself, not borrowed from another stream.
- [ ] Build using normal Visual Studio outputs and record a fresh manual session.
- [ ] Finish, validate, and reopen the new map; compare source records to stored
      graph facts and inspect menu/dialog presentation.
- [ ] Run focused and broader relevant checks; report counts, coverage, acquisition
      timing, remaining failures, and exact qualification boundaries.

Rollback is limited to these collector/presentation changes; there is no storage
migration. Retained bundles remain immutable. New source evidence stays outside Git.

## Results

The live repeat (new frame 4) confirms the geometry-pruning root cause. The popup's
native MSAA `All Fonts` group is `(524,689,282,16439)`, while the popup is only
303 by 941 pixels. The old collector stopped at that group. The repaired collector
returns **658 MSAA records**, including **42 effectively visible records**; 20 visible
All Fonts rows now have their own reported rectangles. Direct UIA Raw/Control/Content
each return just one root record for this popup. None of these MSAA items comes from
the main-window UIA stream. The other 616 MSAA records remain available as offscreen
evidence, not painted as if visible.

The old Colors frame 11 was also replayed through the corrected policy: Colors is
eligible with 140 native records, Main and Font are excluded behind it, and overlay
pairing remains disallowed. The source bundle was not modified.

Fresh recording: seven frames, six stable acquisitions and one changed attempt
retained with its retry; fourteen independent HWND images all returned successfully.
Main behind Font is excluded in frames 5–7; Font behind Colors is excluded in 7.
Colors 7 has 18 UIA Raw, 18 UIA Control, 13 UIA Content, and 119 MSAA records.

The initial finish-map build failed on the raw/derived geometry boundary above.
The bundle was preserved and rebuilt after the fix. The recovered map passes strict
validation and retains **24,233/24,233 raw records field for field**, with zero missing
or extra records and zero overlapping acquisition intervals. This is recovery of
the actual new recording, not a replacement synthetic recording.

The initial dropdown attempt was disturbed while a broader test run was active.
That run was stopped, and the successful dropdown repeat was collected with the
desktop otherwise idle. The changed attempt remains visible as uncertain evidence;
it is not counted as a successful dropdown coverage result.

Provider completeness still needs work: Font's three UIA views time out; MSAA
returns its evidence. Stable captures are not atomic snapshots. Measured acquisition
times are 9.6 seconds (baseline), 47.9 seconds (dropdown repeat), 132.7 seconds (Font),
95.0 seconds (palette), and 91.4 seconds (Colors). These are too slow for fluid use.
Grid behavior was preserved in code but no fresh grid export was performed.

Checks so far: 30 focused presentation/parity cases, 15 MSAA cases, 24 geometry/input
cases, and **479/479 clean Core tests** passed. Seven native Core desktop cases remain
outside this run, as documented in the parent rollout. Final Windows, viewer, and
normal-output qualification follows below.
