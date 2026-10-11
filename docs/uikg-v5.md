# UIKG version 5

`ui-atlas.uikg/5` adds persisted, observation-scoped control facts to the version 4 graph contract. `GraphNode.Observations` retains control state, bundle/frame identity, effective ownership and derivation. These facts are included in the semantic hash. Safe exports omit them.

The schema is [uikg-v5.schema.json](../schemas/uikg-v5.schema.json). Organized and human-readable export schemas also accept the new graph version. Cross-reference and hash integrity remain enforced by the runtime validator.

Version 4 maps remain readable, including maps created during the initial observation rollout that already contain this field. The reader validates their original hash, preserves their content, and upgrades only the format version in memory. Opening a map does not rewrite its database. Version 1 through 3 migrations remain supported.

Older readers reject version 5 as unsupported instead of silently dropping observation facts and reporting a misleading corruption error. Build the recorder and viewer together. Recorder-only builds now build Desktop as a dependency and stage it under the recorder's `viewer` directory; published flat packages remain supported.

Reconstructed scene membership uses the existing state-to-control `contains` edges with `sceneProjection=reconstructed` on the state. Edge evidence identifies the scene frame; a retained control keeps its earlier node evidence and `Observations` fact. Readers project the latest same-bundle control fact at or before that scene frame, and only for explicit scene members. This adds no JSON fields and does not change the v5 hash contract. Raw Data Streams remain the exact per-capture payload.
