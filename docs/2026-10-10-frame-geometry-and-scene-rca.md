# Frame geometry and scene reconstruction: PR #1 amendment

Superseded in part by the approved [raw frame isolation rollout](2026-10-10-raw-frame-isolation-pr-rollout.md):
the historical Raw Data Streams scene-context behavior described below is removed.
Raw Data Streams displays only the selected capture; reconstruction remains in Raw World.

## Reproduction and cause

The open `word-v5.db` viewer uses the retained `word-fixed.mlrec` qualification recording. Frame 1 contains screenshot pixels, no automation. Frame 2 is a partial quick scan (131 entries, `node-limit`); its Font and Font Size entries are `UiAtlas.Cached`, disabled and offscreen. Their coordinates came from another capture. The presentation code exempted cached controls from visibility checks and painted those stale rectangles, including split-button candidates in orange dashes. This is not a uniform screenshot scaling error.

Frame 3 contains 12 current entries, principally ancestry and the Underline split button. It is a control delta based on frame 2. The scene reducer retained earlier knowledge, but build and presentation consumed only current-frame controls. Raw World also suppressed these updates from its observation carousel. The default scene therefore lost the surrounding ribbon.

The initial scan additionally shortened its ribbon budget to 1.1 seconds when a cache had at least 24 entries, and that same cache count disabled alternate UIA views. Historical hints thus reduced acquisition of current geometry. Cache availability is not evidence of current coverage.

test5's `WindowObservationRecorder.NormalizeAutomationBoundsScreen` guards screen/owner-local bounds normalization; `UiKgAppMapReadModel.BuildControlBoundsProjection` intersects recorded rectangles with the owning surface before projecting to local coordinates. UI Atlas already has the latter projection. The regression here occurs before projection: stale cached coordinates are treated as current geometry, and delta records are treated as complete scenes.

## Rollout, in order

1. Preserve source recordings and existing maps. Keep Raw Data Streams immutable and retain provider/cache hints for inspection.
2. Remove cache-count shortcuts from live ribbon acquisition and alternate-view recovery. Verify live Font/Font Size geometry in a fresh disposable Word document.
3. Reconstruct each partial/delta scene from current observations plus compatible earlier observations. Invalidate retained content when its window disappears, changes identity/geometry, or a complete observation replaces it. Persist scene membership separately from original control evidence; never manufacture a new observation for a retained control.
4. Read the same scene membership for Raw World, Semantic World and the contextual AppMap for a raw delta. Report current versus retained controls and each retained control's source frame. Keep source-only inspection available.
5. Exclude historical cache hints from screen overlays/crops. Keep actual visual candidates identifiable with a plain legend; distinguish them from selected or observed controls.
6. Add regression tests for stale cache geometry, partial and complete updates, retained provenance, window invalidation, SQLite round-trip and capture coverage. Replay the supplied recording, inspect frame 2/3, run focused and broader checks, then update the existing draft PR.
7. Final reopen exposed a separate recording defect: while waiting for a manual click, a hover popup inherited the reserved interaction ID, but Stop never committed that interaction. At the sealing boundary, retain every captured frame and clear uncommitted causal references with a capture-health diagnostic. Keep validation strict and preserve committed references; do not invent a click to make the bundle valid. Test both cases and repeat the sealed-bundle reopen check.

## Validation record

The exact proposed snapshot restores with locked packages and builds Release using installed SDK 10.0.401. Focused scene, reader, graph, recorder and repository checks pass **206/206**, including eight new scene cases. The final full Windows suite passes **524/524**, including two new sealed-bundle interaction-reference cases. The full Core run passes **422/426** before the final two ancestor-invalidation cases; its four failures are the previously reproduced native overlay lifecycle and three opaque-grid tests. Both additional cases pass in the final focused run. Offline save/load/export smoke passes. Normal Visual Studio Debug Build also passed; final viewer acceptance is recorded below.

The supplied recording rebuilds to 928 nodes and 1,604 edges, with semantic hash `ff98232545eb0a0d1a7359c027f37b3f9b779907133d2ea158edc0973d23635c` on repeated builds. Frame 2 has 95 scene members. Frame 3 has 105: 12 current and 93 retained from earlier observations. Membership edges describe reconstruction; the retained nodes still carry their original evidence frames. Original recording/map files were not rewritten.

A fresh manual recording used a new blank Word document. Frame 2 now contains live, visible Win32 Font `(522,324,125,24)` and Font Size `(649,324,43,24)` controls, aligned with their screen rectangles. It contains 154 source entries and reports partial coverage. The native ribbon returned 113 controls; the separate adaptive source still reports an opaque-provider gap. The recording was sealed and its map built. This proves the named combo geometry fix, not complete application coverage. Historical frame 2 never recorded live combo geometry; its old cache rectangles are hidden, not repositioned using future observations.

Visible acceptance on the rebuilt historical map: Raw World frame 3 exposes 105 scene members; its Overlay renders 2 current and 90 retained entries after visibility filtering. Raw Data Streams frame 3 still reports 12 original source entries. Its contextual Controls view renders the surrounding ribbon (2 current plus 89 retained), with no cached orange candidates or central ghost boxes. Clearing Scene context shows only the current Underline split-button parts; selecting it again restores the ribbon. Controls and Overlay have different display filters, so these display counts need not equal structural scene membership.

The first fresh recording exposed the uncommitted popup correlation during evidence attachment and was preserved unchanged. After the sealing repair, a second live manual recording reproduced the same hover popup with zero clicks. Its bundle passes strict validation, retains frame 3 with no invented interaction, and records `uncommitted-frame-reference` in capture health. Its SHA-256 is `4dae1775fce5e2998281d61079633d09ab43b76ba54b17bd14fb3c2e3cee9fa2`. The normal recorder executable launches the matching staged viewer, opens this map, attaches its evidence, and shows aligned Font/Font Size boxes in Overlay. The viewer is left open on that result. Private recordings, screenshots and maps are not committed.

Prior-head hosted CI (`83c4d28`) passed the recorder/viewer build staging gate and Windows 522/522, but Core failed six native desktop cases (the local four plus two table-capture cases). Keep PR #1 draft until those baseline CI gates are resolved or accepted. The earlier popup and build checks did not qualify root-scene geometry or contextual delta rendering.
