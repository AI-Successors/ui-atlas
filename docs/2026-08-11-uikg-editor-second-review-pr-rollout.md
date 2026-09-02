# UI KG editor second-review correction PR rollout

Status: implemented and qualified on 2026-08-11.

## Authority and scope

This rollout is driven by the ten-minute recorded review captured on 2026-08-11 after the first standalone UI KG editor parity pass. The recording compares the current standalone explorer with the canonical editor and identifies four remaining mapping-generic defects. The reference implementation is used read-only and only at the AppMap projection, raw ownership, and observed-variant seams recorded in the provenance ledger.

The repository remains a read-only, offline explorer and deterministic recording-to-graph builder. Authoring, Atlas, workflows, execution, approvals, IPC, cloud services, and private recording data remain out of scope.

## Review findings and work items

1. **The five AppMap projections still have the wrong visual semantics.** Controls currently shows labels and outlines instead of control image crops; Structure draws invented parent-child connector lines; Structure Overlay does not use the canonical translucent scene backing. Define the five projections as data, consume the same definition in WPF, and characterize it without WPF.
2. **Selected surfaces render against the whole captured scene.** A popup or contextual menu must show only its own retained pixel rectangle and surface-local controls. Add a bounds-safe surface crop and make every projection use the same local coordinate origin.
3. **Raw World preserves the UIA reporting host instead of the effective owned surface.** Word can report a popup subtree through the main-window provider even when scoped Win32 evidence identifies a separate owned popup. Detect the strongest bounds-matched UIA subtree for each owned surface, move that subtree only in Raw World, preserve Raw Data Streams as captured, and propagate the corrected owner into Semantic World.
4. **Raw World and Semantic World expose Raw Data Streams frame noise as variants.** Keep all captured observations in Raw Data Streams, but hide empty frames, frames whose effective content belongs to a popup, and stable or visually near-identical duplicates from higher-world filmstrips. Preserve deterministic reasons and keep meaningful content changes such as distinct ribbon tabs.
5. **The private Word acceptance artifact must prove the correction.** Rebuild the existing catalog recording without committing it, verify popup control ownership and the reduced main-window filmstrip, and manually inspect the five projections and local popup crop.

## Ordered implementation

### PR 1 - WPF-free projection and variant policy

- Add an AppMap presentation policy for Window, Controls, Overlay, Structure, and Structure Overlay.
- Add bounds-safe projection helpers for clipping absolute evidence and control rectangles to one surface.
- Extend observed variants with visibility, reason, and deduplication lineage.
- Keep Raw Data Streams lossless while applying higher-world empty, popup-frame, stable-content, and visual-content filters.
- Add reader tests for every projection, crop clipping, popup suppression, and duplicate filtering.

### PR 2 - Raw effective-owner materialization

- Build a per-frame effective control-owner assignment from scoped Win32 windows plus UIA runtime ancestry.
- Prefer the richest bounds-matched UIA subtree for an owned window and prevent nested owned surfaces from stealing each other's descendants.
- Keep original per-window UIA in Raw Data Streams and use the effective assignment only for Raw World and Semantic World.
- Build popup fingerprints and raw controls from the reassigned subtree.
- Add synthetic tests where the root UIA provider reports an owned popup subtree and where the popup is nested.

### PR 3 - Surface-local AppMap rendering

- Crop retained evidence to the selected surface before sizing the viewport.
- Render Controls from retained control pixel crops, with a label fallback only when pixels are unavailable.
- Keep Overlay on the undimmed scene, remove invented Structure connectors, and render Structure Overlay over a translucent scene.
- Use the same local origin for control hit targets and Window selection highlights.
- Preserve explicit missing-evidence status and all non-pixel inspection paths.

### PR 4 - Qualification and delivery

- Rebuild and validate the private Word catalog map.
- Inspect raw and semantic ownership counts and visible variant reasons from generated data.
- Run locked Release build/tests, repository boundary checks, offline smoke, deterministic packaging, package compliance, and extracted-package smoke.
- Update README, graph documentation, limitations, qualification evidence, version metadata, SBOM, release instructions, and provenance.
- Audit working tree, reachable content, and release artifacts for excluded content and credential patterns.

## Acceptance criteria

- Window shows the selected surface screenshot; a popup is cropped to the popup rectangle rather than the full Word scene.
- Controls shows retained pixels only inside materialized control rectangles, with no scene backing.
- Overlay shows the scene plus control geometry at full scene opacity.
- Structure shows control geometry without a screenshot and without invented parent-child connector lines.
- Structure Overlay shows the same structure over a translucent scene.
- Raw Data Streams retain their captured reporting owner and full observation list.
- Raw World popup controls are owned by and rendered on the popup when a bounds-matched UIA subtree proves that presentation.
- Semantic World inherits the corrected raw owner and lineage.
- Raw World and Semantic World omit empty, popup-effective-owner, and duplicate-content frames from the default filmstrip while retaining materially different ribbon states.
- Existing valid v2 maps remain readable; rebuilding is required to materialize corrected ownership.

## Test plan

- Pure reader tests for five-mode presentation policy and clipped surface-local rectangles.
- Read-model tests for lossless Raw Data Streams variants, popup-frame filtering, empty filtering, and deterministic stable/visual deduplication.
- Builder tests for main-provider popup subtrees, explicit popup-provider fallbacks, nested popup ownership, fingerprinting, raw-to-semantic lineage, and deterministic rebuilds.
- Existing graph, migration, export, bundle-security, catalog, Windows recorder, and repository-boundary suites.
- Private Word rebuild and manual explorer pass; recording, map, screenshots, titles, and labels remain outside the repository.

## Risks and mitigations

- **Geometric overlap can confuse underlying controls with popup content.** Reassign only a runtime subtree rooted at a control whose bounds closely match the scoped owned window; geometry alone is not enough.
- **The same popup root can be collected through two HWNDs.** Select the richest matched subtree and exclude duplicate fallback roots when better ancestry evidence exists.
- **Nested popups can overlap.** Resolve the smallest/deepest owned surface first and never reassign an already claimed subtree to an ancestor popup.
- **Near-duplicate filtering can hide real state.** Use a high similarity threshold, retain every frame in Raw Data Streams, and record a deterministic hidden reason plus the surviving variant ID.
- **Pixel crops may be absent by privacy policy.** Fall back to bounded labels and geometry without weakening bundle validation or synthesizing pixels.

## Definition of done

- All four recorded defects have implementation and regression coverage.
- The private Word artifact demonstrates surface-local popup pixels, popup-owned controls, and a meaningfully reduced higher-world variant filmstrip.
- All build, test, boundary, offline, packaging, provenance, and content-audit gates pass.
- The rollout records exact measured results and leaves no private recording material in the repository or release package.
- Changes are ready for review without pushing or publishing.

## Execution result

- Implemented effective Raw World control ownership from bounds-matched runtime subtrees while leaving provider-reported Raw Data Streams unchanged. Synthetic coverage proves the reporting owner remains the main native window while Raw World and Semantic World move the popup control to the popup.
- Added deterministic higher-world observation classification with `empty_frame`, `popup_effective_owner_frame`, `duplicate_content`, and `duplicate_visual_content` reasons plus survivor lineage. The rebuilt private Word map retained all 12 representative observations in Raw Data Streams and exposed frames 1, 21, 36, and 51 by default on the main higher-world surface.
- Added a WPF-free five-mode presentation policy and surface clipping. Desktop rendering now crops to the selected surface, uses retained control pixels in Controls, keeps Overlay at full scene opacity, removes Structure connector lines, and uses a 0.72-opacity scene for Structure Overlay.
- Rebuilt and validated a private catalog artifact with 3,443 nodes and 5,627 edges. Seven popup surfaces owned 9, 83, 27, 28, 86, 8, and 13 controls; the main raw surface owned 290 controls. No private recording identifier or content was added to the repository or package.
- Qualified 82 platform-neutral tests and 14 Windows tests, a zero-warning/error Release build, repository boundary checks, the isolated offline smoke flow, package compliance, and extracted-package CLI execution.
- Produced two byte-identical isolated `1.6.0` archives; the final SHA-256 is recorded beside the archive and in the release handoff.
- Signing, installer work, pushing, publishing, and external clean-host operating-system coverage remain outside this rollout's authority.
