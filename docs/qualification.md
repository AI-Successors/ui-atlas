# Windows qualification evidence

This file separates measured coverage from support policy. Passing on one host does not substitute for the remaining operating-system matrix.

## Measured through 2026-08-31

| Area | Environment | Result |
| --- | --- | --- |
| OS and architecture | Windows 11 Pro build 26200, arm64 process | Passed |
| Cross-architecture runtime | Windows 11 Pro build 26200, x64 process under arm64 emulation | Passed seven Windows tests with native-capture and multi-monitor requirements; independent x64 hardware is not implied |
| Desktop topology | three monitors, including negative virtual-desktop coordinates | Passed scoped movement/bounds/DPI test |
| Native pixels | Windows Graphics Capture required by test flag | Passed PNG capture; cancellation also passed |
| Owned popup identity | WPF root plus owned popup | Passed |
| Minimized observation | WPF window minimized during test | Passed |
| UI Automation hang | isolated helper deliberately blocked past deadline | Passed bounded termination |
| Click arming | one-click and two-click release gates, including stale-click rejection and no capture boundary after click one of two | Passed deterministic Windows tests |
| Continuous attended arming | live synthetic target with console input redirected independently | Passed target click without `N`; target stayed foreground through capture, processing-time clicks were ignored, and the recorder rearmed after materialization without console activation |
| Cancellation/retention | discard and retain policies | Passed; retained bundle validated as `cancelled` |
| Local catalog lifecycle | isolated catalog with synthetic recording and map | Passed list, safe export, compatibility export, consumer read, and recoverable recording/map delete |
| Attended end-to-end | repository synthetic target through CLI consent, record, validate, build, inspect | Passed with two native frames, 38 nodes, and 34 edges; a second live run retained root plus owned popup in both frames |
| Explorer second-review parity | rebuilt 52-frame Word recording with 12 statebook representatives and matching evidence | Passed seven effective popup surfaces with 9, 83, 27, 28, 86, 8, and 13 controls; the main surface retains 290 controls and curates 12 observations to four visible higher-world variants (frames 1, 21, 36, and 51); live inspection showed the selected 86-control popup in its 284 x 468 local crop with structure boundaries and no inferred connectors; all five projection compositions and clipping are characterized in platform-neutral tests |
| Explorer third-review fidelity | existing validated Word map and recording; no rebuild or re-recording | Passed live inspection of a 302-control Raw Data Streams main-window frame and the 86-control semantic popup: Controls had no scene underlay and retained interactive pixels, Overlay used the same opaque crops over a subdued scene, Structure used pale fills, Structure Overlay kept geometry dominant, and the primary Main Window to Raw Window to Semantic Window lineage remained on row zero |
| Malicious bundle inputs | traversal/absolute paths, duplicate and case-colliding entries, link metadata, checksums, compression ratio, entry count and byte limits | Passed |
| Main UiAtlas compatibility | clean temporary harness built from source commit `623f581c15e9d415a46d4d4949b09f6570d10ffe`; current Word compatibility export deserialized by the main read model and rebuilt all three derived layers | Passed: Raw Data Streams 4 surfaces/882 controls, Raw World 1 surface/878 controls, Semantic World 1 surface/878 controls; 48 diagnostics were preserved per layer |
| Current implementation gates | working repository, locked dependency graph, Release build, platform-neutral and Windows tests, boundary gate, and offline smoke | Passed: zero build warnings/errors, 296 platform-neutral tests, 294 Windows tests, repository boundary checks, and the complete isolated catalog/build/quality/inspect/export/consume/delete flow |
| CLI command surface | one-shot nested commands plus persistent shell input for help, list, validate, build, inspect, export, consume, and recoverable deletion | Passed default `EXPORT MAP` plus explicit `format=json`, `format=ui-atlas_flat`, and `format=sqlite`; validation and recoverable deletion were exercised for every format |
| Human-readable JSON | private 3,443-node Word map plus synthetic regression | Passed `ui-atlas.map.json/1`: Raw Data Streams 2 windows/19 variants, Raw World 8 windows/19 variants, Semantic World 3 windows/19 variants; every variant owns a controls array and explicit lineage IDs connect worlds |
| Graph JSON compatibility | safe and full `ui-atlas.uikg.export/1` plus legacy flat-v2 reader fixture | Passed exact graph round trip and legacy read compatibility |
| Release package | isolated release-candidate build from the working repository plus extracted CLI execution | Package compliance and extracted help/export-format flow passed; SHA-256 is recorded beside the archive |
| Per-user installer | release `install.ps1` into isolated non-administrator destinations | Passed ten installs without a Start-menu side effect; every installed CLI started successfully |
| Same-host clean profiles | ten separate install roots and ten separate `UI-ATLAS_DATA_HOME` catalogs | Passed 10/10: recording validation, map build/validation, `map quality --strict` with `STATUS READY`, safe export, consumer read, and recoverable map/recording deletion |
| Release content audit | working tree, index, reachable history, generated binaries, and all release ZIP entries | Passed explicit forbidden-content scan and generic credential/token/private-key scan with zero findings in every scope |

Qualification command for a multi-monitor native-capture host:

```powershell
$env:UI-ATLAS_REQUIRE_NATIVE_CAPTURE = '1'
$env:UI-ATLAS_REQUIRE_MULTI_MONITOR = '1'
dotnet test tests/UiAtlas.Core.Windows.Tests/UiAtlas.Core.Windows.Tests.csproj -c Release
```

## Not yet measured

- A clean Windows 10 2004+ x64 host.
- An independent clean Windows 11 x64 host.
- Higher-integrity targets, protected-content windows, and a true DWM-cloaked target; these are expected limitation cases and must produce a recoverable partial result rather than a recorder crash.
- Code signing and installer behavior on an independent clean machine; the same-host per-user installer test passes, but the ZIP remains unsigned.
- The new one-/two-click prompt has deterministic gate coverage but has not yet been manually qualified against a third-party production application.

## Same-host clean-profile substitute

`tools/Test-CleanProfiles.ps1 -Archive <release.zip> -Profiles 10` extracts and installs the release into ten separate application directories and uses ten separate `UI-ATLAS_DATA_HOME` catalogs. Each profile executes help, synthetic recording with two retained PNG frames, validation, map build, `map quality --strict`, safe export, consumer lookup, and map/recording deletion. Any `NEEDS REVIEW` result fails the run.

This is useful evidence for clean startup, state isolation, packaging, and lifecycle regressions. It must not be described as ten independent computers: OS build, hardware, security policy, DPI stack, accessibility providers, and installed runtimes remain shared.
