# UI KG editor third-review fidelity PR rollout

Status: implemented and qualified on 2026-08-11.

## Authority and scope

This rollout is driven by the 2026-08-11 four-minute follow-up review of the standalone UI KG editor. The review accepts Window projection and the popup surface correction from the preceding rollout, then identifies remaining fidelity problems in Controls, Overlay, Structure, Structure Overlay, and the understanding-pipeline layout. The `test5` AppMap renderer and surface-topology builder are read-only behavioral references at only those seams.

The standalone boundary remains unchanged: offline attended capture, deterministic graph construction, read-only exploration, export, and qualification. Product authoring, workflows, runtime execution, approvals, IPC, cloud services, and private recording content remain excluded.

## Review findings and work items

1. **Controls is visually reconstructed from the wrong control set and stacking order.** Large structural frames are being treated as screenshot crops and can cover interactive descendants, which makes the view resemble the whole application screenshot and makes controls appear missing. Match the reference eligibility and z-order rules: structural frames first, ordinary frames next, interactive controls last; crop only reliable interactive controls.
2. **Overlay does not contain the reference control pixels.** It currently draws geometry over the scene. Overlay must use the same 100%-opaque eligible control crops as Controls over a subdued scene, with uncropped structural/fallback controls still inspectable.
3. **Structure lacks readable fill hierarchy.** Ordinary blueprint controls need layer-aware pale fill while large structural frames remain transparent. Structure Overlay needs the same geometry over a substantially subdued scene so the structure remains dominant.
4. **The control universe must be identical across non-Window modes.** Controls, Overlay, Structure, and Structure Overlay must enumerate the same frame-scoped controls; visual eligibility changes only crop/fill treatment, never membership.
5. **The primary understanding lineage is not laid out as a readable lane.** Alphabetical surface ordering sends Main Window to the bottom of Raw World and Semantic World. Assign deterministic topology rows so the main native window, primary Raw Window, and primary Semantic Window remain on row zero, while owned/popup branches occupy subsequent rows.
6. **The private Word artifact must prove the corrections without entering the repository.** Reopen the existing rebuilt map, compare all five modes, verify the primary lineage, and retain exact counts only in qualification documentation.

## Ordered implementation

### PR 1 - WPF-free visual composition contract

- Extend the five-mode presentation policy with scene opacity and blueprint-fill behavior.
- Add pure control classification for reliable crop eligibility, structural-frame detection, interactivity, and deterministic render priority.
- Characterize that Controls and Overlay use identical crop eligibility and all four non-Window modes retain the same control membership.

### PR 2 - AppMap rendering fidelity

- Order controls by structural/interactivity priority and area before rendering.
- Crop only eligible interactive controls; keep crops fully opaque.
- Use pale layer-specific fallback/blueprint fills, with transparent large structural frames.
- Subdue the scene in Overlay and Structure Overlay and preserve surface-local clipping.

### PR 3 - Deterministic understanding-pipeline lanes

- Add an explicit WPF-free row to every pipeline node.
- Keep Application, Process, primary native surface, primary Raw Window, and primary Semantic Window on row zero.
- Allocate owned/popup branches to stable later rows and inherit the Raw row in Semantic World where lineage is unambiguous.
- Draw nodes from assigned rows rather than independent alphabetical column indexes.

### PR 4 - Qualification and delivery

- Add regression coverage for visual policies, control classification/order, and primary-lane topology.
- Rebuild only if graph-generation inputs change; otherwise reuse the existing validated Word map and recording for UI acceptance.
- Run Release tests/build, repository boundary checks, offline smoke, deterministic packaging, package compliance, extracted-package smoke, and working-tree privacy/credential scans.
- Update version, README, architecture, limitations, graph documentation, qualification, release guidance, SBOM, and provenance.

## Acceptance criteria

- Window remains the selected surface screenshot.
- Controls has no scene underlay; large structural controls cannot masquerade as a full-scene crop or cover interactive descendants.
- Every eligible interactive control uses its retained pixel crop in both Controls and Overlay.
- Overlay uses fully opaque control crops over a subdued scene.
- Structure uses readable pale control fills and transparent large structural frames.
- Structure Overlay keeps structure dominant over a subdued scene.
- Controls, Overlay, Structure, and Structure Overlay use the same frame-scoped control membership.
- The primary Main Window to Raw Window to Semantic Window path is horizontally readable on row zero.
- Existing owned/popup branches remain selectable and retain their lineage.
- No private recording, frame, label dump, or screenshot is added to the repository or release package.

## Definition of done

- Every recorded finding has implementation and regression coverage.
- Live Word inspection confirms the AppMap composition and primary topology lane.
- All build, test, boundary, offline, packaging, provenance, and content-audit gates pass.
- Changes are ready for review without committing, pushing, signing, or publishing.

## Execution result

- PR 1 implemented a WPF-free five-mode composition contract, reliable crop eligibility, structural-frame classification, and deterministic stacking priority.
- PR 2 applied that contract in the desktop renderer: Controls is crop-only, Overlay uses the same opaque crops over a 24%-opacity scene, and both structure modes use pale layer-aware fills with large structural frames left transparent.
- PR 3 assigned stable pipeline rows. Live inspection showed the primary Main Window to Raw Window to Semantic Window lineage as a straight row-zero path while popup branches remained independently selectable.
- PR 4 added regressions and updated release evidence for version 1.7.0. The platform-neutral suite passed 84 tests, the Windows suite passed 14 tests, the full Release build passed with zero warnings and errors, the repository-boundary gate passed, and the offline synthetic flow passed.
- Live acceptance reused the existing validated private Word map and recording; no recapture or map rebuild was needed because this rollout changes only reader/presentation behavior. The inspected Raw Data Streams main surface contained 302 controls and the semantic popup contained 86 controls. All five modes were checked on the existing artifact.
- Deterministic packaging, extracted-package smoke, and final content scans are the release-artifact gates performed after this document is finalized. No private recording, frame, label dump, screenshot, or machine-local path is retained here.
