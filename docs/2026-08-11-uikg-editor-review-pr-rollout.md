# UI KG editor review parity PR rollout

Status: implemented and qualified locally on 2026-08-11.

## Authority and scope

This rollout is driven by the recorded side-by-side review of the canonical UI KG editor and the standalone editor. The review covers the public, offline Raw Data Streams, Raw World, and Semantic World exploration path only. Atlas, authoring, runtime, workflow, and project/workspace behavior remain outside this repository.

The canonical implementation is a behavioral and algorithmic reference. Reused logic is restricted to the mapping-generic surface classification, observation-variant, topology-selection, and AppMap projection seams recorded in the provenance ledger.

## Review findings and work items

1. **Cross-column inspection is broken.** A topology node can currently be inspected only when its surface belongs to the active understanding level. Keep the selected horizon and inspection layer separate so every visible node in Application, Process, Raw Data Streams, Raw World, or Semantic World is selectable.
2. **Observed variants are represented incorrectly.** Build variants from the selected surface's observation evidence, preserve frame/control membership, render them as the canonical horizontal filmstrip, and keep the selected frame synchronized with AppMap and properties.
3. **The AppMap has an invented vertical surface list.** Remove it. Surface navigation belongs to Environment Hierarchy and Understanding Pipeline; the AppMap body belongs entirely to the selected surface/variant.
4. **Evidence is not loaded with catalog maps.** `map open <map-id>` must resolve and pass the matching catalog recording automatically. Manual evidence attachment remains only for maps opened by path or deliberately detached evidence.
5. **The five AppMap projections are not distinct enough.** Implement Window, Controls, Overlay, Structure, and Structure Overlay as five explicit projections with correct screenshot, control, and hierarchy-line composition.
6. **Environment Hierarchy contains controls.** Keep its progressive level groups, but list only surface/window instances and variant groups. Controls remain selectable in AppMap and discoverable by search.
7. **Raw/semantic types and titles diverge from canonical mapping.** Apply the canonical popup-before-tool classification rule for borderless transient hosts, materialize stable raw popup instances by observed control/layout fingerprint, preserve lineage, and expose canonical type names and meaningful surface titles.
8. **Selection synchronization lacks a testable read-model seam.** Move cross-layer selection, observed-variant construction, and hierarchy projection decisions into platform-neutral reader types and cover them without WPF.
9. **Existing maps need an explicit compatibility answer.** The corrected builder remains on the v2 graph contract, but affected recordings must be rebuilt to receive corrected classification and naming. The editor must continue to open valid existing v2 maps.

## Existing seams

- `RecordingGraphBuilder` already materializes all three observed layers and lineage in one deterministic pass.
- `UiMappingReadModel` already isolates graph interpretation from WPF.
- `UiEvidenceReader` already validates a matching recording bundle and reads canonical frame entries.
- `ExplorerWindow` already owns the progressive topology, hierarchy, filmstrip, five projection controls, and properties pane.
- `LocalArtifactCatalog` already binds recording and map IDs to safe local paths.
- The canonical reference contains mapping-generic surface classification, observed-variant, cross-layer topology selection, and AppMap projection behavior that can be adapted without importing unrelated architecture.

## Ordered implementation

### PR 1 — canonical read-model semantics

- Add an explicit inspection level to topology nodes.
- Add evidence-keyed observed variants with per-frame control membership.
- Add deterministic resolution from any pipeline node to its inspection surface(s).
- Add a window-only hierarchy projection.
- Add read-model regression tests for progressive horizons, cross-column resolution, variants, and hierarchy contents.

### PR 2 — raw and semantic materialization parity

- Reorder and generalize native classification so borderless/transient popup evidence wins over the generic tool-window style bit.
- Derive raw popup identity from stable native grouping plus visible control/layout fingerprint.
- Use canonical raw surface type names and deterministic display summaries.
- Preserve source-stream, raw, semantic, owner, evidence, and control lineage.
- Add Word-like synthetic fixtures for a normal window, dialog, and borderless tool-hosted popup.

### PR 3 — explorer interaction parity

- Separate active horizon from inspection layer.
- Make every visible topology node selectable and keep topology, hierarchy, filmstrip, AppMap, and properties synchronized.
- Remove the vertical surface list.
- Render the observed-variant filmstrip horizontally with an explicit selected state.
- Keep Environment Hierarchy surface-only.

### PR 4 — AppMap and evidence parity

- Auto-attach the catalog recording from `map open <map-id>`.
- Render five explicit AppMap projections:
  - Window: retained screenshot and selected-element highlight.
  - Controls: control geometry and labels without screenshot pixels.
  - Overlay: screenshot plus control geometry.
  - Structure: control hierarchy and geometry without screenshot pixels.
  - Structure Overlay: screenshot plus hierarchy and geometry.
- Make missing or intentionally omitted evidence an explicit status, not a silent blank surface.

### PR 5 — qualification and delivery

- Add regression coverage for builder classification, variant membership, selection, catalog evidence resolution, and malformed/missing evidence.
- Rebuild and validate the supplied local Word recording/map without committing its data.
- Run locked restore, Release builds, all tests, boundary checks, offline smoke, deterministic packaging, and extracted-package smoke.
- Update README, format documentation, qualification evidence, known limitations, provenance, SBOM version, and release instructions.

## Acceptance criteria

- With Semantic World selected, clicking a Raw Data Streams or Raw World node inspects that node without changing the visible horizon.
- Clicking any surface instance in Environment Hierarchy or any surface-bearing topology node updates the same selection, properties, variant filmstrip, and AppMap.
- Environment Hierarchy contains application and surface/window items only; controls do not appear as tree children.
- A stable normal/raw/semantic surface with multiple observations exposes those observations as horizontal variants.
- A borderless transient Word-style host is classified as `RawPopupWindow`, not `RawToolWindow`.
- The selected variant determines the screenshot and the controls shown for that frame.
- `map open <map-id>` displays retained evidence without an Evidence-file prompt when the matching recording exists.
- All five projections are visually and behaviorally distinct as defined above.
- Maps and exports remain deterministic, versioned, provider-agnostic, offline, and consumable without the desktop application.

## Test plan

- Reader unit tests for cross-layer topology resolution, observation variants, window-only hierarchy, and per-frame controls.
- Builder golden tests for normal, dialog, borderless popup, duplicate labels, dynamic control state, and lineage.
- Evidence tests for matching, missing, mismatched, malformed, and screenshot-free bundles.
- CLI process-argument test for automatic catalog evidence attachment.
- Release build and all platform-neutral and Windows tests.
- Repository boundary, source/provenance, package compliance, no-network smoke, and extracted-package synthetic flow.
- Manual Word acceptance pass using a newly rebuilt map from the existing private local recording; no recording data enters the repository or release artifact.

## Risks and mitigations

- **Canonical logic is large and coupled.** Adapt only the narrow mapping-generic rules above and characterize them with standalone tests.
- **Existing v2 maps retain old classifications.** Keep them readable and document that rebuilding is required for corrected derived worlds.
- **Screenshot data may be absent by policy.** Preserve all non-pixel projections and show an explicit privacy/evidence status.
- **Dense UIA surfaces can overwhelm the canvas.** Keep deterministic caps, clipping, and selected-item priority while reporting counts honestly.
- **Semantic naming is observational, not authored truth.** Prefer deterministic evidence summaries and retain lineage instead of fabricating intent.

## Definition of done

- Every review finding above has an implementation and regression test, or an explicitly documented lower-severity limitation.
- The supplied Word flow can be rebuilt and inspected through all three supported worlds and five AppMap projections.
- All required repository, security, provenance, build, test, offline, and packaging gates pass.
- The rollout status and qualification evidence record the exact result.
- A local commit is prepared for review; nothing is pushed or published.

## Execution result

- Implemented all nine review work items across the deterministic builder, WPF-free reader, local catalog, CLI, and focused desktop explorer.
- Rebuilt the supplied local Word recording into a valid 4,448-node/7,324-edge graph without committing the recording or map.
- Manually verified automatic evidence attachment, earlier-column selection under the Semantic World horizon, the 16-frame main-window filmstrip, Raw World popup inspection, and distinct pixel/no-pixel projections.
- Added five new regression tests and tightened one pre-existing validator test so its selected layer is deterministic; 79 platform-neutral and 14 Windows tests pass.
- Remediated a real owner-lineage collision exposed by the Word recording by including stable owner-surface identity in owned-surface keys.
- Remediated package path/Git-state drift; independent repository and clean-copy 1.5.0 archives are byte-identical.
- The extracted release ZIP completed the offline synthetic recording, validation, build, export, and independent-reader flow.
- Working tree, index, reachable history, generated binaries, and package contents passed explicit forbidden-content and generic secret scans with zero findings.
