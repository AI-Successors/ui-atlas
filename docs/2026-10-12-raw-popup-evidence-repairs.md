# Raw popup evidence and observation selection repairs

Follow-up to the [raw-stream PR rollout](2026-10-11-raw-data-streams-parity-pr-rollout.md),
authorized by “proceed with repairs” after reviewing the 04:45 feedback video.

## Evidence and root causes

- At 00:21 the window-family highlight disappears when an observation changes.
  Topology selection compares only the representative surface ID; hierarchy
  selection already recognizes all surfaces in that family.
- At 00:49 Main/frame 2 shows geometry without crops. Acquisition was interrupted
  and changed. Withholding pixel/geometry pairing is intentional, but the tiny
  status line does not adequately distinguish incomplete evidence.
- At 02:54 Font/frame 6 paints palette pixels into underlying Font controls.
  `ReadWindow` correctly supplies a contextual frame image containing the palette,
  but the renderer incorrectly reuses that image as each control's pixel source.
- At 03:22 Font's overlay shows palette pixels with Font geometry drawn across
  them, while the palette's separately captured provider records are absent.
  The font dropdown can appear better because its parent MSAA query happens to
  return menu descendants. That is not a reliable source-selection contract.

## Goals

1. Keep the window family selected through card navigation.
2. Clearly label incomplete/uncertain captures without deleting their evidence or
   hiding successful foreground provider attempts such as Colors.
3. Separate contextual frame pixels from per-window control-image evidence.
   Missing or covered legacy pixels must never become another window's crops.
4. Offer owned popup layers from the exact same bundle and frame, with explicit
   window targets, independent provider selectors, and preserved control ownership.
   Clip geometry behind foreground windows in contextual views.
5. Replay the retained `word-quality` bundle to qualify the actual reported cases.

## Non-goals

No capture schema migration, provider fusion, geometry fabrication, cross-frame
inheritance, automatic dialog traversal, new recording, or changes to grids and
exports. Capture latency and modal UIA timeouts remain separate work. A stable
acquisition flag does not establish an atomic screenshot/provider snapshot.

## PR rollout

- [x] A: map topology highlighting to all family surface IDs; regress multiple
      observations and resumed recordings without matching unrelated families.
- [x] B: add prominent incomplete-capture guidance and card status, preserving
      geometry-only inspection and original attempts.
- [x] C: give control crops an isolated per-HWND image source; withhold legacy
      crops intersecting occluders; cover offsets, missing images and overlap.
- [x] D: render separately labeled same-frame popup layers with provider choices;
      retain failures and empty attempts, exclude dialogs and foreign sessions.
- [x] E: focused and broader Core checks, normal Visual Studio build, retained-map
      replay and visual inspection; record precise results and remaining limits.

## Rollback

Revert this presentation-only follow-up. Bundles and graphs remain unchanged and
compatible. Preserve unrelated working-tree changes. No source evidence enters Git.

## Validation

The clean Core run passes **483/483 tests**, excluding the same seven native
desktop cases (MapperOverlayWindowTests and GridExplorerNativeWindowTests) as the
previous qualification. Four new cases include two actual WPF renderer runs that
distinguish a blue composite frame from red owner and green popup image planes,
verify clipping and unstable-frame crop suppression, and navigate every family
variant. Other cases cover exact session/frame/window scope, empty attempts and
legacy occlusion. No Windows capture code changed; that suite was not rerun.

Clean Release solution build: zero warnings/errors. Repository boundary checks
passed. The disposable snapshot contains only these changes plus the installed SDK
feature-band setting. Normal Visual Studio Build produced **UI Atlas Core 2.0.89**:
eight projects succeeded, zero failed, six up-to-date. Normal outputs include the
user's pre-existing working edits; the clean qualification excludes them.

Reopened the managed `word-quality` map in the normal staged editor and verified:

- Main/frame 2 keeps the purple Main Window family highlight, labels its card
  `Incomplete capture`, and shows the prominent warning while withholding overlay.
- Font/frame 6 Controls uses Font's `frame-000006-window-D2EC4.png`; it no longer
  paints palette labels/swatches into underlying Font controls. The popup has its
  own image source, target `0xCB14D4`, provider selector and green outlines.
- Font/frame 6 Overlay shows palette controls from the popup's own 80-record UIA
  Raw attempt; underlying Font outlines are clipped at the popup. The selector
  also retains the popup's other attempts. Its 361 accepted records remain owned
  by the popup, with their original evidence references.
- Colors/frame 7 remains a separate observation; source data was not reassigned
  to Font or Main. Popup presentation follows immediate recorded owners only.

A read-only replay audit covered ten retained observations in frames 2, 4, 6 and 7.
The map still has **24,233 raw records**. The recording bundle SHA-256 remains
`b96c20698e489a8a05028930feb81815f76c0ab7da7efa2432e610d631ab2a45`.
Local screenshots (`font-controls.png`, `font-overlay.png`, `main-incomplete.png`)
and the audit are under `%USERPROFILE%/.codex/tmp/ui-atlas-popup-repair-audit/`.
The existing map was opened through the catalog import UI; no recapture or raw
bundle rewrite was performed.

These checks establish the reported viewer repairs. They do not establish atomic
captures, repair Font UIA timeouts, reduce observation latency, or certify a fresh
live grid export. Previously documented native CI failures remain separate gates;
the PR remains draft and unmerged.
