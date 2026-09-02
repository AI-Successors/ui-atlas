# Reproducible release procedure

1. Start from a clean clone on Windows with .NET SDK 10.0.200.
2. Run `dotnet restore UiAtlas.Core.slnx --locked-mode`. For a deliberately offline verification using an already populated package cache, add `-p:NuGetAudit=false`; this disables only the remote advisory lookup for that verification and does not replace the online vulnerability gate below.
3. Run Release build, all tests, `tools/Test-RepositoryBoundary.ps1`, and `tools/Smoke-Offline.ps1`.
4. Run `tools/Package.ps1 -Version 1.9.0`. The script performs one symbol-free solution build in an isolated intermediate/output tree, publishes only from that build, removes Git-state drift from assembly informational metadata, sorts entries, and normalizes timestamps.
5. Run `tools/Test-CleanProfiles.ps1 -Archive <release.zip> -Profiles 10`. This checks ten isolated installs/catalogs on the current host; it does not replace independent-host qualification.
6. Compare the generated ZIP SHA-256 across two clean environments, including one source tree that has already been built and tested. Prior build state must not alter the archive.
7. Run external worktree, index, reachable-history, secret, license, SBOM, and vulnerability audits. Keep sensitive deny terms and audit reports outside the repository.
8. Code-sign only after the unsigned artifact hash is approved. Signing identity and publication are intentionally outside this repository.

The package is architecture-neutral, framework-dependent, and requires the .NET 10 Windows Desktop Runtime. Its Windows OCR enrichment path also ships the exact SDK-generated `Microsoft.Windows.SDK.NET.dll` / `WinRT.Runtime.dll` pair declared in `ui-atlas.deps.json`; package checks reject a missing, misplaced, unpinned, or undisclosed projection assembly. Run the CLI with `ui-atlas.cmd` (or `dotnet ui-atlas.dll`) and the explorer as `dotnet UiAtlas.Core.Desktop.dll`. No runtime restore or network access is performed.
