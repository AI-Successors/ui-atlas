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

- [x] Correct foreground-card eligibility; regress interrupted Colors with a
      successful response and preserve the background-window/overlay safeguards.
- [x] Correct MSAA subtree traversal; verify All Fonts descendants are captured
      from the popup itself, not borrowed from another stream.
- [x] Build using normal Visual Studio outputs and record a fresh manual session.
- [x] Finish, validate, and reopen the new map; compare source records to stored
      graph facts and inspect menu/dialog presentation.
- [x] Run focused and broader relevant checks; report counts, coverage, acquisition
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

## Final qualification

- **479/479 clean Core tests and 536/536 clean Windows tests passed.** Focused runs
  also passed: 30 presentation/parity, 15 MSAA, and 24 geometry/input cases. Seven
  native Core desktop cases remain outside the local run, as documented in the
  parent rollout. Counts from focused runs overlap the broader suites.
- Clean Release solution build: zero warnings/errors; repository boundary checks
  passed. The clean snapshot excludes unrelated working changes; its SDK selection
  matches the installed feature band.
- Normal Visual Studio Build produced **UI Atlas Core 2.0.84**: 11 succeeded,
  zero failed, three up-to-date. Recorder and staged viewer identify implementation
  commit `b4a83fbc82cd74fcff55005345959ac511cd7ec3`. Normal outputs include the user's
  pre-existing working changes.
- Replayed the recorder's actual enrichment, build, speculative projection and
  session-registration path against the preserved bundle. It produced a strict-valid
  map with 26,252 nodes. This followed the observed Finish Map failure and recovery;
  it is not a claim that the original Finish Map click succeeded.
- Opened the new map in the normal editor and inspected Raw Data Streams. Dropdown
  frame 4 selects its own 658-record MSAA attempt; rectangles cover the visible All
  Fonts rows. Colors frame 7 shows its independent image with 18 UIA Raw records
  (16 with usable geometry). Font frame 7 is excluded behind Colors. Main offers
  frames 1–4, excluding background frames 5–7. No Image streams family is shown.
- Imported through the editor into Saved Maps as `word-quality`, including its
  bundle, and reopened the managed copy. Repeated the raw audit on that copy:
  **24,233 expected, 24,233 stored, zero missing/extra**. The source bundle SHA-256
  remains `b96c20698e489a8a05028930feb81815f76c0ab7da7efa2432e610d631ab2a45`.

Hosted CI for implementation `b4a83fb` is **not green**: run
[38199238326](https://github.com/AI-Successors/ui-atlas/actions/runs/38199238326)
failed six native desktop cases (three mapper-overlay cases and three opaque-grid
cases). The same six case names fail on the preceding `644` baseline run
[38195356089](https://github.com/AI-Successors/ui-atlas/actions/runs/38195356089).
These remain qualification gates; they are not counted among the local passes.

Local deliverables: `%LOCALAPPDATA%/UiAtlas/Core/maps/word-quality.db` and
`%LOCALAPPDATA%/UiAtlas/Core/recordings/word-quality.mlrec`. Private logs, field-level
audits and editor screenshots (`dropdown-editor.png`, `colors-editor.png`) are under
`%USERPROFILE%/.codex/tmp/ui-atlas-word-quality-20261012/`; they are not committed.

## Assessment and remaining work

The specific dropdown coverage, missing Colors card and finish-map raw-geometry
failures are repaired and qualified with actual Word evidence. This does **not**
establish complete or fast capture for every provider. MSAA ownership is reported as
unverified; a direct query target is not a proved ancestry chain. UIA gives the font
dropdown only a root, and all three UIA views time out on Font. Raw capture retains
those outcomes without inventing control data.

Next work should prioritize the timed-out modal UIA queries and observation latency,
then improve provider ownership verification and qualify native grid behavior on
an interactive desktop. Keep per-provider attempts, timing and failure evidence
visible during that work. Draft PR #1 remains unmerged; hosted CI qualification is
separate from the local test results above.
