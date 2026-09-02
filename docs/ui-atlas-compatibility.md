# Main UiAtlas compatibility publication

The compatibility publisher turns a validated local `ui-atlas.uikg/2` full-evidence graph into the current v5 `uikg/vnext` recording/import seam. The `authoring` object retains the three application, window, and control collections, while `observationPackages` carries the package-specific Win32 and UIA streams needed to rebuild Raw Data Streams and Raw World instead of treating the publication as authoring-only.

Use a stable project identifier that matches the destination project:

```powershell
ui-atlas export map <map-id> format=ui-atlas_flat --project-id my-stable-project

dotnet run --project src/UiAtlas.Core.Cli -c Release -- export-ui-atlas sample.db `
  --out ui_knowledge_graph_vnext.json `
  --project-id my-stable-project `
  --acknowledge-sensitive-identities

dotnet run --project src/UiAtlas.Core.Cli -c Release -- validate-ui-atlas-export ui_knowledge_graph_vnext.json
```

The command writes the checksum sidecar first and publishes the JSON only after strict in-memory validation; it then reopens and verifies both files. A missing or mismatched sidecar fails validation. Output is deterministic for the same graph and project ID. IDs are stable across recordings when the durable Raw/Semantic identities are unchanged. Controls are coalesced across observed states before publication. The document declares adapter version, privacy profile, source graph version, and source semantic hash.

The publication contains identity-bearing UI labels, selectors, product identity, class names, geometry, and sanitized raw observation mirrors. It therefore requires explicit acknowledgement and is not the public sharing default. Observation artifacts preserve relative evidence names and stable recording/package keys but omit session folders, screenshot bytes, executable paths, live HWND/PID values, actions, scopes, and runtime configuration.

For a new or empty destination project, place the file under the exact name `ui_knowledge_graph_vnext.json` in the project data directory, then open that project. If the destination already has its authoritative SQLite graph, use main UiAtlas's supported compatibility-import/save path; the authoritative SQLite file takes precedence over the JSON migration source. Do not overwrite an existing project graph without first preserving it.

The adapter is pinned to v5 and validated by [ui-atlas-vnext-compat-v1.schema.json](../schemas/ui-atlas-vnext-compat-v1.schema.json). A main UiAtlas schema change requires a new adapter version and conformance run. Missing product-version metadata does not block exploratory import, but it can prevent the destination from treating the map as enforcement-qualified; the exporter never fabricates that metadata.
