# Open-source readiness checklist

This checklist distinguishes implemented guarantees from evidence that still requires a real third-party application or another physical Windows host.

## Implemented and automatically checked

- Every accepted manual interaction ends with a full-root, popup, or dialog result frame. A failed result is recorded explicitly and appears in `map quality`.
- The initial PNG and post-click screenshot fallback are persisted independently of slow UI Automation enrichment.
- The recorder shows elapsed time, the active capture/build/save stage, and a specific result after each click.
- Exact duplicate screenshots are counted; higher-level duplicate states are collapsed while Raw Data Streams remain auditable.
- Tables can carry `tableRow`, `tableColumn`, headers, and cells from native providers or visual grid recovery. `map quality` prints the final structured-cell count.
- The release ZIP is portable and includes a one-command, per-user `install.ps1` installer.
- `samples/UiAtlas.Core.Consumer` demonstrates an autotest/AI-agent query returning selectors, actions, and observed destinations.
- Storage, retention, screenshot sensitivity, safe export, and recoverable trash behavior are documented in `privacy.md`.
- `Test-CleanProfiles.ps1` installs and exercises the packaged application in ten isolated application/data profiles on one Windows host. Every profile must build a synthetic map with retained PNG evidence and pass `map quality --strict` before its map and recording are moved to that profile's trash.

## Evidence still required before a public release claim

- Record three genuinely different screens in a representative line-of-business application and verify three promoted screen states, result frames for every click, stable top controls, and a structured table.
- Record the agreed Excel workflow and one unrelated complex Windows application with the same acceptance criteria.
- Capture a short, unedited proof video from those three runs. Do not substitute synthetic screens for product evidence.
- Run the release package on independent Windows 10 x64 and Windows 11 x64 machines. Ten isolated profiles on one host detect state leakage and installation defects, but they are not ten independent machines.
- Code-sign the final ZIP/executables before broad non-technical distribution.

## Release gate

A candidate is ready for a public beta only when:

1. all tests, package checks, offline smoke, and ten clean-profile runs pass;
2. `map quality --strict` passes for the three demo maps;
3. the demo video shows the original screen beside the resulting map without cuts hiding recorder delays;
4. no recording containing customer data is committed or published.
