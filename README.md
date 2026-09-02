# UiAtlas Core

UiAtlas Core is an offline-first Windows toolkit for manually recording one selected desktop application and its owned popups, preserving Raw Data Streams, deterministically building Raw World and Semantic World UI Knowledge Graph layers, inspecting and diffing them, and consuming a lossless JSON export from an independent library.

The supported v1 scope is deliberately narrow: attended manual capture, Windows 10 version 2004 or later and Windows 11, Win32 window metadata, bounded UI Automation, scoped window screenshots, immutable recording bundles, observed mapping entities and transitions, SQLite, JSON, a command-line tool, and a read-only WPF explorer. It does not perform actions in applications or provide authoring, execution, remote-control, or background-monitoring features.

## Prerequisites

- Windows 10 2004+ or Windows 11
- .NET 10 Windows Desktop Runtime for the release ZIP
- .NET SDK 10.0.200 only for source builds
- A desktop session at the same integrity level as the target application

## Install the release ZIP

Extract the ZIP and run one command from the extracted `ui-atlas-core` folder. Installation is per-user and does not require administrator rights:

```powershell
powershell -ExecutionPolicy Bypass -File .\install.ps1
```

This copies the portable application to `%LOCALAPPDATA%\Programs\UiAtlas` and creates a **UiAtlas Recorder** Start-menu shortcut. The extracted folder also remains portable: run `ui-atlas.cmd` directly if installation is not wanted.

UiAtlas does not download components, upload recordings, or require an account. If the required Windows Desktop Runtime is missing, the installer stops with an explicit message instead of leaving a partial installation.

Restore and build using the committed lock files:

```powershell
dotnet restore UiAtlas.Core.slnx --locked-mode
dotnet build UiAtlas.Core.slnx -c Release --no-restore
dotnet test UiAtlas.Core.slnx -c Release --no-build --no-restore
```

When all locked packages are already available in the local NuGet cache and the machine is intentionally offline, add `-p:NuGetAudit=false` to the restore command. Run the normal restore or the vulnerability command in `docs/release.md` while online to refresh advisory data before release.

## Record an application

```powershell
dotnet run --project src/UiAtlas.Core.Cli -c Release -- list windows
dotnet run --project src/UiAtlas.Core.Cli -c Release -- recording start 0x123456
dotnet run --project src/UiAtlas.Core.Cli -c Release -- list recordings
dotnet run --project src/UiAtlas.Core.Cli -c Release -- list maps
```

`recording start` asks for explicit consent and takes one full baseline observation. Each attended click is stored immediately, then UiAtlas persists either a complete resulting screen, an owned popup/dialog, or an explicit failed/unobserved interaction. Pixels, UI Automation, visual geometry, OCR, and bounded native verification use independent lanes where the target permits it; provider delays do not erase the screenshot fallback. Exact visual duplicates are retained as raw evidence but collapsed into one higher-level map state. The recorder shows elapsed time, the active capture/build stage, and a concrete completion or review message. Finish waits up to two seconds for queued popup work; Cancel ends only the current recording and leaves the toolbar available for another session.

Artifacts are kept under `%LOCALAPPDATA%\UiAtlas\Core`: immutable `.mlrec` recordings, SQLite maps, JSON exports, and a recoverable catalog trash. The recording and generated map share the displayed ID. Deletion commands move an artifact to that trash:

```powershell
dotnet run --project src/UiAtlas.Core.Cli -c Release -- recording validate <recording-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map build <recording-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map validate <map-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map quality <map-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map inspect <map-id> world streams
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map inspect <map-id> world raw
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map inspect <map-id> world semantic
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map open <map-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map export json <map-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map delete <map-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- recording delete <recording-id>
dotnet run --project src/UiAtlas.Core.Cli -c Release -- map delete all
dotnet run --project src/UiAtlas.Core.Cli -c Release -- recording delete all
```

`map quality` reports deduplicated screens, semantic controls, structured table cells, duplicate screenshots, partial scans, and clicks without a confirmed result screen. Add `--strict` in CI to return a failing exit code when review is required.

`export map` defaults to the identity-bearing, human-readable JSON map. Every world contains `windows`; every window contains `variants`; and every variant contains its observed `controls`. Lineage arrays connect windows, variants, and controls back to the preceding world. Other supported formats are explicit:

```powershell
ui-atlas export json validate C:\exports\sample.json
ui-atlas export json delete "$env:LOCALAPPDATA\UiAtlas\Core\exports\sample.json"
ui-atlas export map <map-id>
ui-atlas export map <map-id> format=json
ui-atlas export map <map-id> format=ui-atlas_flat
ui-atlas export map <map-id> format=sqlite
ui-atlas export map validate C:\exports\sample-map.json
ui-atlas export map delete "$env:LOCALAPPDATA\UiAtlas\Core\exports\sample-map.json"
```

`format=json` is the default human-readable `ui-atlas.map.json/2` projection, including ordered interaction sessions, merged routes, affordances, and separate negative examples. `format=ui-atlas_flat` produces the existing v5 `uikg/vnext` compatibility document. `format=sqlite` publishes a lossless standalone copy of the authoritative graph database. All three retain identity-bearing UI information and require confirmation; unattended scripts can supply `--acknowledge-sensitive-identities`. `map export json` remains the separate privacy-safe graph interchange command. Catalog recordings, maps, and exports are moved to catalog trash. Export deletion accepts only a direct child of the managed exports directory; unrelated, nested, external, or linked paths are rejected.

## Interactive command shell

The release ZIP includes `ui-atlas.cmd`. Run it without arguments to enter a persistent, DISKPART-style command shell:

```text
UiAtlas Core 2.0.0
Type HELP for a list of commands, or EXIT to leave.

UI-ATLAS> HELP
UI-ATLAS> LIST
UI-ATLAS> LIST WINDOWS
UI-ATLAS> HELP MAP
UI-ATLAS> MAP INSPECT <map-id> WORLD RAW
UI-ATLAS> EXIT
```

Command names are case-insensitive. `HELP`, `HELP LIST`, `HELP RECORDING`, `HELP MAP`, and `HELP EXPORT` show aligned command summaries; entering a command family without a subcommand shows the same family help. Every shell command also works as a one-shot command, for example `ui-atlas list windows` or `ui-atlas recording start 0x123456`.

Direct-path commands remain available for scripting and testing:

```powershell
dotnet run --project src/UiAtlas.Core.Cli -c Release -- record --hwnd 0x123456 --out sample.mlrec
dotnet run --project src/UiAtlas.Core.Cli -c Release -- build sample.mlrec --out sample.db
dotnet run --project src/UiAtlas.Core.Cli -c Release -- validate sample.db
dotnet run --project src/UiAtlas.Core.Cli -c Release -- inspect sample.db --query button
dotnet run --project src/UiAtlas.Core.Cli -c Release -- export sample.db --out sample.safe.json
dotnet run --project src/UiAtlas.Core.Cli -c Release -- export-ui-atlas sample.db --out ui_knowledge_graph_vnext.json --project-id my-stable-project --acknowledge-sensitive-identities
dotnet run --project src/UiAtlas.Core.Cli -c Release -- validate-ui-atlas-export ui_knowledge_graph_vnext.json
dotnet run --project src/UiAtlas.Core.Cli -c Release -- diff old.db sample.db
dotnet run --project src/UiAtlas.Core.Cli -c Release -- open sample.db
dotnet run --project samples/UiAtlas.Core.Consumer -c Release -- sample.safe.json
dotnet run --project samples/UiAtlas.Core.Consumer -c Release -- sample.db --query "Save" --json
```

The second sample is the integration seam for an autotest or AI agent: it returns semantic control IDs, stable selectors, supported actions, evidence counts, and observed destination states without giving the sample permission to click the application.

The explorer opens the canonical graph without workspace context. Its three progressive understanding levels use the same mapping concepts as the main editor: Raw Data Streams (lossless per-observation native windows and UIA), Raw World (fused effective surface ownership), and Semantic World (interpreted reusable surfaces and controls). Environment Hierarchy contains surface/window instances and `Sessions → Steps → Source → Results`. Every visible surface-bearing topology node is selectable even when it belongs to an earlier column. The primary Main Window, Raw Window, and Semantic Window remain on one readable row-zero lineage while owned and popup branches occupy later rows. Higher-world filmstrips hide empty, popup-owned, and duplicate observations while preserving every captured frame in Raw Data Streams. Window shows the selected surface pixels. Controls shows opaque retained pixels for eligible interactive controls without a scene underlay; large structural frames remain behind descendants. Overlay uses those same control crops over a subdued scene. Structure uses pale layer-aware fills without inferred connectors, and Structure Overlay keeps those boundaries dominant over a subdued scene. Trace shows full-frame source/action/result evidence; Routes shows merged observed paths plus dashed affordances with unknown destinations. `map open <map-id>` automatically attaches the matching validated catalog recording when it exists; a direct-path map can use **Evidence** or `--evidence`. A safe export has no screenshot linkage and cannot display pixels.

For a direct local launch, the desktop executable also accepts `graph.db --evidence recording.mlrec`.

For a capture-free deterministic smoke test, replace the record command with:

```powershell
dotnet run --project src/UiAtlas.Core.Cli -c Release -- synthetic-record --out sample.mlrec
```

## Privacy warning

Recording can retain pixels, window titles, accessibility labels, window handles, process identity, and input timing. Printable key identities and literal typed text are suppressed by default. The safe JSON export removes screenshot references, application-provided labels/properties, raw stable keys, source linkage, coordinates, build time, and canonical IDs. Generic kinds and topology can still be sensitive. Review every artifact before sharing it. Full-evidence and main UiAtlas compatibility exports each require explicit acknowledgement.

See [architecture](docs/architecture.md), [recording format](docs/recording-bundle-v1.md), [UI KG v4 format](docs/uikg-v4.md), [Interaction Trace](docs/interaction-trace.md), [main UiAtlas compatibility](docs/ui-atlas-compatibility.md), [privacy model](docs/privacy.md), [threat model](docs/threat-model.md), [qualification evidence](docs/qualification.md), [known limitations](docs/known-limitations.md), and [security policy](SECURITY.md).

## License and provenance

The repository carries the license in [LICENSE](LICENSE). Newly authored files and behavioral-reference dispositions are recorded in [the provenance ledger](provenance/files.csv). Runtime and test dependencies are listed in [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) and the [CycloneDX SBOM](sbom/ui-atlas-core.cdx.json).

## Authors and maintainers

UIAtlas was created and developed by **Daniel Kornev** and **Irina Nikitenko**.

## Authors and maintainers

UIAtlas was created and developed by **Daniel Kornev** and **Irina Nikitenko**.

- **Daniel Kornev** — [LinkedIn](https://www.linkedin.com/in/danielkornev/)
- **Irina Nikitenko** — [LinkedIn](https://www.linkedin.com/in/irina-nikitenko-07598657/)

For questions about the project, architecture, or collaboration, please contact the authors through LinkedIn.
