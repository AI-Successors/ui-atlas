# Raw menu context, provider scope and optional labels

Follow-up to the [raw-stream parity rollout](2026-10-11-raw-data-streams-parity-pr-rollout.md),
authorized by the 22:47 video and “watch, understand, outline fixes, fix”.

## Observed problems and evidence

- **00:14–01:21:** test5's Main Window observations retain the captured open menu.
  UI Atlas prefers an isolated HWND image for every window, removing its separate
  popup HWND from the picture. Its legacy fallback also masks all other windows,
  including owned menus. Both choices lose captured menu context.
- **01:21–01:59:** the borderless window appears to contain reassigned UIA controls.
  The supplied map's frame 3 actually retains 80 records from
  `uia.raw/0xE12978`, queried against that popup, all with verified ancestry.
  Main-window queries are separate. The popup's immediate recorded owner is Font
  (`0x322A54`), not the main window. test5's example has one directly reported
  popup record; that count is not a rule to discard richer direct provider output.
  The editor offers every frame's provider in each window's dropdown/properties,
  obscuring this distinction and allowing irrelevant selections.
- **02:00–03:12:** fallback metadata labels overlap Font's captured controls.
  Controls mode adds labels whenever a crop is absent, without a user preference.
  It also uses control crops on unstable frames even though Overlay correctly
  declines to combine such pixels and provider geometry.

In test5, `UiKgAppMapReadModel` builds raw controls from each frame using
`ResolveReportedRawOwnerSurface`, retaining that frame's screenshot reference.
`UiKgAppMapWindow` renders that recorded screenshot in Window mode. UI Atlas must
likewise distinguish a raw image containing a menu from automation ownership.

## Goals

1. Show recorded menus with their immediate owning window, including cascading
   menus and portions extending outside the owner rectangle.
2. Keep Font/Colors and other independent dialogs separate. Do not propagate a
   popup through a dialog to the application's root owner.
3. Show exactly the selected window's reported provider attempts, including empty
   and failed attempts. Keep all other sources available in Frame streams.
4. Show provider target and scope verification explicitly. Preserve richer direct
   popup observations; never assign main-window controls by geometric overlap.
5. Add **Show labels**, unchecked by default in Raw Data Streams. Tooltips and
   properties remain available. Unstable observations show raw geometry without
   borrowing screenshot crops as evidence of synchronized controls.

## Non-goals

- No scene reducer, cross-frame pixel inheritance, owner inference from proximity,
  provider fusion, deduplication or dialog-tab exploration in Raw Data Streams.
- No rewriting captured bundles or manufacturing new screenshots from independent
  HWND planes. Menu context uses the already recorded frame bytes, cropped to the
  recorded window/menu regions; unrelated covering windows remain masked.
- No changes to grid acquisition, retained tiles, stitching, export or permissions.
- No promise to recover a menu that was absent from the recorded pixels. Missing
  screenshot coverage and interrupted acquisition remain visible.

## PR units and acceptance

- [x] **A — raw image context:** choose the captured frame when recorded Win32 owner
  chains establish a visible borderless popup under the selected window. Clip to
  the union of those recorded regions, use their common screen-space origin, and
  retain isolated HWND images for windows without menu context. Test main menus,
  Font-owned menus, cascades, dialogs, missing/partial images and negative origins.
- [x] **B — provider provenance:** scope the attempt selector and window properties
  to the selected HWND. Label query target and verification counts; preserve empty
  attempts and keep all providers in Frame streams. Test coincident geometry and
  different query targets without reassignment.
- [x] **C — canvas presentation:** make labels opt-in in all raw control modes;
  prevent fallback labels from bypassing the checkbox. Disable control crops when
  capture stability is unknown/changed. Validate checkbox and mode transitions.
- [ ] **D — qualification:** run focused reader, storage and renderer regressions,
  inspect the retained Word recording in the updated editor, build normal Visual
  Studio output, and update the existing draft PR with exact validation limits.

## Rollback

Changes are confined to evidence presentation. Revert the scoped commit to restore
the previous viewer; recording schemas, source bundles and graph contents remain
compatible. Preserve all unrelated working-tree changes.

## Results

- A clean task snapshot passes **471/471 Core tests**, excluding the seven native
  desktop cases already qualified separately in the parent rollout. This includes
  ten new menu/provenance/presentation cases. Full clean Release solution build:
  zero warnings/errors. The disposable snapshot uses the installed SDK feature
  band; unrelated working-tree changes are excluded.
- Live inspection of the retained Word map confirms Font/frame 3 displays its
  captured palette, including the portion below Font's bounds. Font/frame 4 remains
  clean and Colors/frame 4 remains separate. Switching to Colors selects its own
  UIA attempt (18 records), with 140 total records still retained for that window.
- Font/frame 2 Controls shows no added metadata labels by default; enabling and
  disabling Show labels adds and removes them. Borderless/frame 3 shows 80 verified
  popup records and only its four provider attempts, including three failed empty
  attempts. Its interrupted capture supplies geometry without screenshot crops.
- No capture or graph serialization code changed. Existing maps and recordings
  are read directly; no recapture is needed for menus present in their frame pixels.
- Normal Visual Studio output and hosted PR update: pending final delivery.
