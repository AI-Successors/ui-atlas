# UI Knowledge Graph v1

This format is superseded by [UI Knowledge Graph v2](uikg-v2.md). Readers migrate v1 into an explicitly labeled `legacy-observed` layer; they do not infer Raw World or Semantic World claims absent from v1.

`ui-atlas.uikg/1` is a neutral observed-mapping graph. It contains application, window, surface, state, and control nodes; containment and observed-transition edges; typed properties; evidence references; hierarchy; topology; and build lineage.

It intentionally contains no executable behavior. Every observed state/control has evidence. Full-evidence references must match the graph source-bundle identifier and use sequence-bound observation and screenshot paths; readers reject cross-bundle, cross-sequence, and out-of-namespace references. Every parent and edge endpoint must exist. IDs are globally unique. The `semanticHash` covers deterministically ordered nodes and edges. The SQLite file is the local authoritative build artifact; JSON is the lossless interchange representation.

SQLite v1 has three allowlisted tables (`metadata`, `nodes`, `edges`) and three indexes. Each node/edge row carries its complete canonical JSON so order and property representation round-trip losslessly. `PRAGMA user_version` is 1. Readers reject unexpected schema objects, integrity failures, foreign-key failures, oversized files/rows, invalid JSON, dangling references, and duplicate IDs.

Export profiles:

- `safe/1` removes screenshot references, all application-provided labels and properties, raw stable keys, and source bundle identifiers while retaining generic kinds, topology, bounds, and pseudonymous identifiers.
- `full-evidence/1` preserves all canonical evidence references and requires explicit acknowledgement in the CLI.

The JSON Schema is [uikg-v1.schema.json](../schemas/uikg-v1.schema.json). A new required concept, identity algorithm, or lossy field change requires a new major format identifier. Migrations always write a new file and validate it before atomic publication.
