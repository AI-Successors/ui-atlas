param(
  [string]$Configuration = 'Debug',
  [string]$MSBuildAssembly
)
$ErrorActionPreference = 'Stop'
$repository = Split-Path -Parent $PSScriptRoot
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('ui-atlas-viewer-build-' + [Guid]::NewGuid().ToString('N'))
$buildRoot = Join-Path $scratch 'build'
$project = Join-Path $repository 'src/UiAtlas.Core.Cli/UiAtlas.Core.Cli.csproj'
[string[]]$buildCommand = if ($MSBuildAssembly) { @($MSBuildAssembly) } else { @('msbuild') }
$recorder = Join-Path $buildRoot "bin/UiAtlas.Core.Cli/$Configuration/net10.0-windows10.0.19041.0"
$desktop = Join-Path $buildRoot "bin/UiAtlas.Core.Desktop/$Configuration/net10.0-windows10.0.19041.0"

function Build-Recorder([string]$Target) {
  & dotnet @buildCommand $project -restore "-t:$Target" -nologo -v:quiet "-p:Configuration=$Configuration" "-p:UiAtlasIsolatedBuildRoot=$buildRoot" '-p:StopRunningUiAtlasProcesses=false'
  if ($LASTEXITCODE) { throw "Recorder-only $Target failed." }
}

function Assert-Viewer {
  foreach ($required in @('UiAtlas.Core.Desktop.dll', 'UiAtlas.Core.Desktop.runtimeconfig.json', 'UiAtlas.Core.Desktop.deps.json', 'Assets/explorer-icon-export.png')) {
    if (-not (Test-Path -LiteralPath (Join-Path $recorder "viewer/$required"))) { throw "Viewer dependency missing: $required" }
  }
  foreach ($file in Get-ChildItem -LiteralPath $desktop -Recurse -File) {
    $relative = [IO.Path]::GetRelativePath($desktop, $file.FullName)
    $staged = Join-Path $recorder "viewer/$relative"
    if (-not (Test-Path -LiteralPath $staged) -or (Get-FileHash -LiteralPath $staged).Hash -ne (Get-FileHash -LiteralPath $file.FullName).Hash) {
      throw "Viewer dependency differs: $relative"
    }
  }
  foreach ($component in @('Contracts', 'Build', 'Storage', 'Reader')) {
    $file = "UiAtlas.Core.$component.dll"
    if ((Get-FileHash -LiteralPath (Join-Path $recorder $file)).Hash -ne (Get-FileHash -LiteralPath (Join-Path $recorder "viewer/$file")).Hash) {
      throw "Recorder/viewer mismatch: $component"
    }
  }
}

Build-Recorder Build
Assert-Viewer
# Simulate an out-of-date staged reader, then verify ordinary Build refreshes it.
Set-Content -LiteralPath (Join-Path $recorder 'viewer/UiAtlas.Core.Contracts.dll') -Value 'stale'
Build-Recorder Build
Assert-Viewer
Build-Recorder Rebuild
Assert-Viewer
Write-Output 'Passed: recorder-only Build, repeated Build repair, and Rebuild stage a complete matching viewer.'
Write-Output "Isolated outputs: $scratch"
