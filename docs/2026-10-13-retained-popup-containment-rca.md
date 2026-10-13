# Retained popup containment failure

## Report and reproduction

The Word recording `20261013-201130-winword-614f702d.mlrec` finalized but failed map construction with two `graph.containment` errors. Its bundle passes validation and contains 28 representative observations and 19 interactions. Replaying the same offline-enrichment and graph-building path reproduces both exact edge IDs:

- `edge_64e1e418c519540f516e3314`: a pane observed in frame 4 on HWND `0x50EAE` was retained into frame 6 under a different Raw World surface.
- `edge_37a73f5293b2715fb64558c6`: a pane observed in frame 7 on HWND `0x60EAE` was retained into frame 8 under a different Raw World surface.

The original bundle SHA-256 is `40e4bc0acaf9377f869338f1dece1381201139b04f621f5bbce2a543e610282e`.

## Root cause

The scene reducer retains missing controls across incomplete responses while the native window remains compatible. Separately, the graph builder identifies popup surfaces using their observed content. That content can change without changing the HWND or native bounds.

When constructing state membership, the builder checked retained controls' effective HWND and whether they had already been materialized. It did not check the materialized graph surface. Consequently, the new popup state claimed an old control whose declared `rawSurfaceId` belonged to the earlier popup surface. The integrity validator correctly rejected those edges. This is a Raw World reconstruction bug; it does not indicate corrupted Raw Data Streams or a failed recording.

## Repair scope

Goal: only carry a materialized control into a state belonging to that control's original Raw World surface. Preserve same-surface retention, original frame evidence, and complete raw provider records.

Non-goals: change popup identity rules, invent current observations for retained controls, weaken validation, change capture/providers, or alter grid extraction.

- [x] Add synthetic regressions for changing popup surfaces, stable dialog surfaces, and merged sessions with overlapping frame numbers.
- [x] Restrict retained state membership to matching graph surface identity.
- [x] Verify the regression fails before the repair and passes afterward.
- [x] Replay the retained Word bundle; save and reopen the resulting map with hash and integrity validation.
- [x] Run the Core suite and normal application build; recover the managed map and session entry.

## Validation

- Before the fix, both changing-popup regression cases failed with `graph.containment`; both stable-dialog cases passed. After the fix all four pass, including merged recordings with overlapping frame numbers. Original observations remain at frame 1, and Raw Data Streams does not acquire fabricated frame-2 controls.
- Clean Core suite: **487/487 passed**. The same seven native desktop cases (`MapperOverlayWindowTests` and `GridExplorerNativeWindowTests`) excluded in the previous qualification remain excluded; this is not a claim that the entire desktop suite ran. Windows capture tests were not rerun for this graph-builder-only change.
- The retained bundle passes validation. Replay of the actual offline-enrichment/build path produces **36,301 nodes and 44,851 edges**. SQLite save/reopen and graph validation pass with semantic hash `c6e961502299b4189309e11200c2026b5a13913a8d9fb0a602c10e0ea40af9cb`.
- Normal Visual Studio Debug build produced **UI Atlas Core 2.0.91**: 14 projects succeeded, zero failed. An initial background NuGet restore diagnostic appeared; the solution build itself completed successfully. This working-tree build also includes the user's pre-existing unrelated edits; the Core tests ran against a clean HEAD snapshot plus this repair and the installed-SDK selection.
- The normal build's `map build` and `map validate` commands now succeed for the reported recording. The managed map was recovered under its original ID. The existing empty session manifest was backed up, then its recording reference was restored using `LogicalMapSessionStore.AddRecording`, preserving its other contents.
- Original recording SHA-256 is unchanged. Repository boundary checks and `git diff --check` pass. Diagnostic files remain local; the regression uses synthetic controls and includes no recorded screenshots or provider payloads.

The repair preserves controls on their original surfaces and fixes invalid state membership. It does not establish complete UIA/MSAA coverage or change the popup fingerprint policy. Missing provider records and capture timing remain separately inspectable.
