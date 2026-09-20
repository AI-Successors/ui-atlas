param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$project = Join-Path $root 'src\UiAtlas.Core.Cli\UiAtlas.Core.Cli.csproj'
$output = Join-Path $root "src\UiAtlas.Core.Cli\bin\$Configuration\net10.0-windows10.0.19041.0"
if (-not (Test-Path -LiteralPath (Join-Path $output 'ui-atlas.exe'))) {
  throw 'Build the CLI with its apphost before running this test.'
}
$scratch = Join-Path $root ('artifacts\build process cleanup\' + [Guid]::NewGuid().ToString('N'))
$target = Join-Path $scratch 'output'
$neighbor = Join-Path $scratch 'output-other'
$helper = Join-Path $target 'migration-firebird'
$processes = [Collections.Generic.List[Diagnostics.Process]]::new()

function Start-IdleCli([string]$Directory, [string]$HostKind = 'apphost', [string]$Executable = 'ui-atlas.exe') {
  $start = [Diagnostics.ProcessStartInfo]::new()
  $start.FileName = if ($HostKind -eq 'apphost') { Join-Path $Directory $Executable } else { 'dotnet' }
  $start.WorkingDirectory = $Directory
  $start.UseShellExecute = $false
  $start.CreateNoWindow = $true
  $start.RedirectStandardInput = $true
  $start.RedirectStandardOutput = $true
  $start.RedirectStandardError = $true
  $start.EnvironmentVariables['UI-ATLAS_DATA_HOME'] = Join-Path $scratch 'catalog'
  if ($HostKind -eq 'relative') { $start.Arguments = '.\ui-atlas.dll' }
  if ($HostKind -eq 'exec') { $start.Arguments = 'exec "' + (Join-Path $Directory 'ui-atlas.dll') + '"' }
  $process = [Diagnostics.Process]::Start($start)
  $processes.Add($process)
  $deadline = [DateTime]::UtcNow.AddSeconds(10)
  do {
    if ($process.HasExited) { throw "Fixture exited: $($process.StandardError.ReadToEnd())" }
    $process.Refresh()
    if (@($process.Modules | Where-Object { $_.ModuleName -eq 'ui-atlas.dll' }).Count -gt 0) { return $process }
    Start-Sleep -Milliseconds 50
  } while ([DateTime]::UtcNow -lt $deadline)
  throw 'CLI fixture did not load.'
}

function Invoke-CleanupTarget([string[]]$Properties = @()) {
  & dotnet msbuild $project -nologo -v:minimal -t:StopRunningUiAtlasBuildOutput "-p:TargetPath=$target\ui-atlas.dll" @Properties
  if ($LASTEXITCODE) { throw 'Cleanup target failed.' }
}

try {
  foreach ($directory in @($target, $neighbor, $helper)) {
    New-Item -ItemType Directory -Force -Path $directory | Out-Null
    Get-ChildItem -LiteralPath $output | Copy-Item -Destination $directory -Recurse -Force
  }
  Copy-Item -LiteralPath (Join-Path $helper 'ui-atlas.exe') -Destination (Join-Path $helper 'ui-atlas-migrate-firebird.exe')

  $otherNative = Start-IdleCli $neighbor
  $otherDotnet = Start-IdleCli $neighbor 'relative'
  $native = Start-IdleCli $target
  $secondNative = Start-IdleCli $target
  $relativeDotnet = Start-IdleCli $target 'relative'
  $execDotnet = Start-IdleCli $target 'exec'
  $adapter = Start-IdleCli $helper 'apphost' 'ui-atlas-migrate-firebird.exe'
  $expectedStops = @($native, $secondNative, $relativeDotnet, $execDotnet, $adapter)

  foreach ($properties in @(
    '-p:DesignTimeBuild=true', '-p:StopRunningUiAtlasProcesses=false',
    '-p:IsTestProject=true', '-p:IsCrossTargetingBuild=true', '-p:OutputType=Library', '-p:OS=Unix'
  )) {
    Invoke-CleanupTarget @($properties)
    foreach ($process in $expectedStops) {
      if ($process.HasExited) { throw "Excluded build stopped a process: $properties" }
    }
  }

  Invoke-CleanupTarget
  foreach ($process in $expectedStops) {
    if (-not $process.WaitForExit(5000)) { throw "Matching output process survived: $($process.Id)" }
  }
  foreach ($process in @($otherNative, $otherDotnet)) {
    if ($process.HasExited) { throw 'Cleanup stopped a process from another output directory.' }
  }
  Invoke-CleanupTarget
  Write-Output 'Build process cleanup checks passed: native, multiple instances, dotnet relative/exec, copied adapter, path isolation, skip conditions, and repeated cleanup.'
}
finally {
  foreach ($process in $processes) {
    if (-not $process.HasExited) { $process.Kill(); $process.WaitForExit(5000) | Out-Null }
    $process.Dispose()
  }
  $resolvedScratch = [IO.Path]::GetFullPath($scratch)
  $artifactRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts')) + [IO.Path]::DirectorySeparatorChar
  if (-not $resolvedScratch.StartsWith($artifactRoot, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Test cleanup escaped the artifacts directory.'
  }
  if (Test-Path -LiteralPath $resolvedScratch) { Remove-Item -LiteralPath $resolvedScratch -Recurse -Force }
}
