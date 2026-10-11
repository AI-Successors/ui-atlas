# Raw provider geometry blocked map finalization

Status: implemented and qualified; retained recording recovered in the normal
catalog. Amendment to draft PR #1. The existing native Core test failures remain
open as described below.

## Root cause

The newly retained 53-frame Word recording fails through the production CLI build
path with the same raw-control IDs reported by the recorder. Its raw streams
contain 46 controls with X, Y, width and height all equal to `Int32.MaxValue`.
These provider sentinel values are data, not usable screen rectangles.

The raw-stream amendment began preserving original provider controls in graph
observations. Recording validation permits these numeric payloads, but graph
validation applied the derived-control area limit (16 million pixels) to them.
The resulting generic `graph.observation` message mentioned frame evidence and
privacy instead of the actual geometry incompatibility. The failure occurs before
the map is saved; the retained bundle is intact. The earlier tests used ordinary
rectangles and therefore missed this real-provider boundary case.

## Fix and rollout

1. Add a regression for raw sentinel, oversized, overflowing and negative provider
   bounds. Prove the sentinel case fails before the repair.
2. Separate raw payload validity from drawable geometry. Keep original coordinates
   and frame evidence in Raw Data Streams, mark unavailable geometry explicitly,
   and keep unusable rectangles out of rendering. Preserve strict geometry checks
   for derived worlds, evidence uniqueness, privacy and graph hashes.
3. Test SQLite reopen, safe export, frame isolation and rejection of malformed
   derived observations. Replay the exact private recording through production
   build/validation/export paths, checking that all raw records survive.
4. Build the normal Visual Studio outputs, recover the user's map from the retained
   bundle, verify reopening, and update the existing draft PR. Preserve the original
   recording, session metadata backups and unrelated working-tree edits.

## Qualification

The five unusable-rectangle regression cases failed before the fix. The sentinel
case threw the same graph-observation validation error as the private recording.
They now build, retain the original rectangle, survive SQLite reopen and safe
export, and produce neither a drawable rectangle nor a screenshot highlight.
Additional checks reject null payload geometry, oversized text, duplicate or
missing frame evidence, raw observations in safe exports, and oversized derived
geometry. Normal clipping and negative monitor coordinates remain supported.

The graph marks unusable raw geometry as unavailable without deleting the source
control or changing its reported bounds. The viewer reports how many entries lack
usable geometry and bounds its canvas when the selected surface has no usable
rectangle. Screenshot highlighting checks geometry before translating coordinates.
No provider retry or another frame's geometry is substituted.

| Check | Result |
| --- | --- |
| Pre-fix geometry regression | 5/5 failed as expected |
| Focused graph, reader, geometry and evidence suite | 138/138 passed |
| Final exact proposed snapshot Release solution build | Passed |
| Final exact proposed full Core suite | 436/440 passed; same four previously reproduced native failures |
| Final exact proposed full Windows suite | 527/527 passed |
| Repository boundary and offline save/export/import smoke | Passed |
| Normal Visual Studio 2026 Debug solution build | 14 succeeded, zero failed; recorder build 47 |

The four Core failures remain the mapper overlay lifecycle test and the three
opaque-grid traversal cases documented in the earlier rollout. There are no new
Core failures. The exact proposed snapshot excludes unrelated working-tree edits;
SDK 10.0.401 was invoked explicitly for its committed SDK pin.

The copied real recording builds and validates through the production CLI: 13,997
nodes and 19,203 edges. A temporary private harness then invokes the same enriched
input loader, graph builder, saved-prediction projector and SQLite save/reopen code
used by Finish Map. Including 192 applicable saved predictions produces 14,189
nodes and 19,589 edges, semantic hash
`e72ccafa90117d6dda56071b3653969c0ae53d9db26c98e7832928617fa3aa92`.
The isolated replay and the recovered catalog map have the same hash.

A complete comparison of `(frame, stream, original control payload)` proves all
11,548 raw records survive exactly, with zero missing or extra records. All 46
sentinel rectangles remain inspectable and are marked unavailable for drawing.
This includes the exact first failing raw-control ID from the user's error.

The recovery created the previously absent map, registered its retained recording
using the normal session store, and preserved every other session field. Session
metadata was backed up first. The source recording's SHA-256 is unchanged. The
normal Debug CLI validates the recovered map, and its staged viewer opens it with
evidence attached. Interactive inspection switched to Raw Data Streams and the
main Word window's frame 7: 472 recorded entries, 11 without usable geometry, and
pixels explicitly attributed to frame 7. There was no map-open validation error.

No new Word recording was needed or claimed: qualification replays the exact
retained recording that failed. Private evidence, the recovery harness, build
outputs and screenshots are not committed. The PR remains draft and unmerged.
