# AppMap observed-variant strip

## Goal

Make the observed variants available at a glance in AppMap and make switching between them direct. The current frame ComboBox and previous/next controls hide the available observations until the selector is opened. The reference design shows the variants as a horizontal strip above the canvas.

## Interaction and presentation

- Place an **Observed variants** label and a single-row, horizontally scrollable list of variant cards between the AppMap mode controls and the canvas.
- Give each card a stable, concise name from the variant, its frame number and control count, and the evidence surface dimensions when available. Include a short bundle identifier when needed to distinguish frames from separate recordings; expose full details in the tooltip and accessible name.
- Select a variant by activating its card. Preserve the current selected variant when rebuilding the strip, and visibly mark the selected card with the AppMap selection color and border.
- Keep variant selection synchronized with the evidence image, controls, hierarchy, and properties already driven by the selected variant.
- Support keyboard navigation across cards, including previous/next and first/last selection. Bring the selected card into view when selection changes.
- Show a clear empty state when the current surface has no observed variants. Keep the strip usable at narrow window widths through horizontal scrolling.

## Scope and constraints

- Change only AppMap's variant-selection presentation and navigation. Keep the underlying variant filtering, ordering, evidence selection, and synchronization semantics intact.
- Do not alter graph construction, map persistence, or evidence attribution.
- Do not add screenshot or recording data to the repository.

## Acceptance criteria

- [x] Variants appear as selectable cards in a horizontal strip above the canvas, without opening a dropdown.
- [x] The active card is obvious and selection renders the matching frame and controls.
- [x] Long variant collections can be browsed horizontally, and keyboard users can change selection and reach off-screen cards.
- [x] Separate recordings with the same frame sequence remain distinguishable.
- [x] Empty and narrow-window states remain usable.
- [x] Implementation compiles and received a focused code review; test execution status is recorded separately.

## Implementation record

- `ExplorerWindow` now renders an **Observed variants** strip with one focusable card per observation. Each card shows the variant name, frame, control count, available evidence dimensions, and a short capture ID; its tooltip and accessible name include the full capture ID.
- Clicking a card updates the selected evidence and existing AppMap projections. Left/Right select adjacent cards, Home/End select the first/last card, and off-screen selections scroll into view. The selected card is highlighted, empty surfaces show an explicit message, and the in-progress legacy-grid repair summary remains visible on the relevant card.
- Release build passed with **0 warnings and 0 errors** using .NET SDK **10.0.401** and an isolated output directory. The repository's `global.json` pins 10.0.200, which is not installed in the standard SDK directory. A build into the normal output folder encountered the open qualification viewer's file lock; that user process was left running, and the isolated build completed successfully.
- Tests were not run for this UI-only amendment.
