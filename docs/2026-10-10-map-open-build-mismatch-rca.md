# Newly recorded map cannot open: recorder/viewer build mismatch

## Evidence and cause

The normal Debug recorder validates the retained Word qualification map, while its `open` command launches the older Desktop Debug output and reproduces exactly `graph.semantic-hash@metadata.semanticHash: Semantic hash does not match graph content.` The recorder's Contracts assembly was built October 10 UTC; the viewer's Contracts assembly was built October 9 UTC (October 8 local). Their SHA-256 hashes differ. The old viewer ignores the new `GraphNode.Observations` JSON member, so its recomputed hash omits retained content.

The CLI project has no build dependency on Desktop. `OpenMapViewer` falls back from the CLI directory to a sibling Desktop output directory without checking compatibility. Building the recorder alone therefore leaves an old viewer in use. Observation fields were also added without changing graph version 4, which lets an incompatible reader mistake a newer graph for its own format.

The previous rollout qualified matching isolated binaries, but did not qualify a recorder-only Visual Studio build followed by the recorder's normal Open Map path. This is a rollout defect. The newly reported 1,295-control map has not yet been located in the accessible catalog; reproduction currently uses the unchanged retained Word qualification map, which is valid under the matching reader.

## PR rollout amendment

1. Preserve the old viewer and map evidence, and reproduce the exact normal-launch error before editing.
2. Make Desktop a build dependency of the recorder, collect its actual output directory (including isolated output roots), and stage the complete viewer under the recorder output. Keep normal published flat layouts supported. Remove the unverified sibling-output fallback and reject mismatched graph assemblies with a build-specific diagnostic.
3. Write observation-bearing graphs as version 5. Read existing version 4 graphs without changing their content hash; validate the original hash before upgrading the version in memory. Retain compatibility with older supported graph versions. Never repair a hash simply to make a map open.
4. Add regression coverage for launch resolution, mismatched assemblies, version 4 observation preservation and corruption rejection. Exercise recorder-only Build, repeated Build and Rebuild with isolated outputs.
5. Rebuild through normal Visual Studio, then open the unchanged qualification map through the recorder. Verify successful viewer loading and preservation of file hashes. Check the user's exact map if its path becomes available. Update this record and the existing draft PR with results and remaining boundaries.

## Execution

The recorder now builds Desktop as a dependency and stages its complete output under `viewer`. Launch resolution uses that directory or the flat published layout and checks matching Contracts, Build, Storage and Reader assemblies. Resume recording resolves back to the parent recorder directory. Build cleanup includes the exact staged viewer path, so an open viewer cannot leave replacement binaries locked.

New graphs use version 5. Existing v4 maps load after validation with their original hash and contents intact. The graph/export schemas describe the new observations, and corrupted hashes still fail closed. The normal-launch fix does not alter a map to accommodate an old reader.

| Check | Result |
| --- | --- |
| Exact pre-fix normal launch | Reproduced the reported semantic-hash dialog on a valid retained Word map |
| Normal Visual Studio Debug solution build | Passed; a subsequent build also stopped the staged viewer and refreshed it successfully |
| Recorder-only isolated Build, stale-copy refresh, Rebuild | Passed; complete dependency files and graph assembly hashes match; added to CI |
| Proposed snapshot locked restore and Release solution build | Passed with SDK 10.0.401, no warnings/errors |
| Final focused Release checks | 123/123 passed, including 18 new launch/resume/version cases and repository boundaries |
| Full Core suite before the four additional resume cases | 412/416 passed; the same four previously recorded native-window failures |
| Full Windows suite | 522/522 passed |
| Repository boundary and offline smoke | Passed |
| Version 5 graph and full/safe organized export schema validation | Passed against the retained Word replay |

The unchanged v4 qualification map opens through the recorder after the normal Visual Studio build. Its file SHA-256 remains `9a057fd222bf6cf06556b056e2829ca1f7754a4da597a9eb0725e51eef349076` before and after opening. The new viewer exposes the retained 83/25/21/14-control popup observations. A separate v5 replay builds and validates; private recordings and databases remain outside Git.

The four Core failures are `RealWindowOverlayRefreshSelectionCaptureAndTeardown` and the three `ExploreOpaqueGridTraversesAndRestoresWithoutReadingText` cases already documented in the original rollout. They remain release gates. The newly reported 1,295-control map is not present in the accessible catalog, so exact-file verification remains pending its path; no claim is made that it was repaired or opened. The matching-binary reproduction and correction are verified independently. The existing PR stays draft and unmerged.
