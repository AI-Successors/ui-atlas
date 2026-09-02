param([string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
$cliExe = Join-Path $root "src\UiAtlas.Core.Cli\bin\$Configuration\net10.0-windows10.0.19041.0\ui-atlas.exe"
$cliDll = Join-Path $root "src\UiAtlas.Core.Cli\bin\$Configuration\net10.0-windows10.0.19041.0\ui-atlas.dll"
$consumerExe = Join-Path $root "samples\UiAtlas.Core.Consumer\bin\$Configuration\net10.0\UiAtlas.Core.Consumer.exe"
$consumerDll = Join-Path $root "samples\UiAtlas.Core.Consumer\bin\$Configuration\net10.0\UiAtlas.Core.Consumer.dll"
if (-not (Test-Path -LiteralPath $cliExe) -and -not (Test-Path -LiteralPath $cliDll)) { throw 'Build the solution before running the smoke test.' }
if (-not (Test-Path -LiteralPath $consumerExe) -and -not (Test-Path -LiteralPath $consumerDll)) { throw 'Build the consumer before running the smoke test.' }
function Invoke-Cli([Parameter(ValueFromRemainingArguments)] [string[]]$Arguments) {
  if (Test-Path -LiteralPath $cliExe) { & $cliExe @Arguments } else { & dotnet $cliDll @Arguments }
  if ($LASTEXITCODE) { throw "CLI command failed: $($Arguments[0])" }
}
function Invoke-Consumer([string]$GraphPath) {
  if (Test-Path -LiteralPath $consumerExe) { & $consumerExe $GraphPath } else { & dotnet $consumerDll $GraphPath }
  if ($LASTEXITCODE) { throw 'Consumer failed.' }
}
function Invoke-CliExpectFailure([Parameter(ValueFromRemainingArguments)] [string[]]$Arguments) {
  $priorPreference = $ErrorActionPreference
  try {
    $ErrorActionPreference = 'Continue'
    if (Test-Path -LiteralPath $cliExe) { & $cliExe @Arguments 2>$null } else { & dotnet $cliDll @Arguments 2>$null }
    if ($LASTEXITCODE -eq 0) { throw "CLI command unexpectedly succeeded: $($Arguments[0])" }
  }
  finally { $ErrorActionPreference = $priorPreference }
}
function Invoke-InteractiveSmoke {
  $commands = "HELP`nLIST`nMAP HELP`nEXIT`n"
  $start = [Diagnostics.ProcessStartInfo]::new()
  # Exercise the same framework-dependent invocation used by the packaged ui-atlas.cmd launcher.
  $start.FileName = 'dotnet'
  $start.Arguments = '"' + $cliDll.Replace('"','\"') + '"'
  $start.UseShellExecute = $false
  $start.RedirectStandardInput = $true
  $start.RedirectStandardOutput = $true
  $start.RedirectStandardError = $true
  $process = [Diagnostics.Process]::Start($start)
  $process.StandardInput.Write($commands)
  $process.StandardInput.Close()
  $output = $process.StandardOutput.ReadToEnd()
  $errorOutput = $process.StandardError.ReadToEnd()
  $process.WaitForExit()
  if ($process.ExitCode -ne 0 -or $errorOutput) { throw "Interactive CLI shell failed: $errorOutput" }
  if (-not ($output -match 'UI-ATLAS>') -or -not ($output -match 'BUILD')) { throw 'Interactive CLI help output was incomplete.' }
}
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('ui-atlas-offline-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
try {
  $env:HTTP_PROXY = 'http://127.0.0.1:9'
  $env:HTTPS_PROXY = 'http://127.0.0.1:9'
  ${env:UI-ATLAS_DATA_HOME} = Join-Path $scratch 'catalog'
  Invoke-InteractiveSmoke
  Invoke-Cli HELP MAP
  Invoke-Cli MAP HELP
  $recording = Join-Path ${env:UI-ATLAS_DATA_HOME} 'recordings\synthetic-smoke.mlrec'
  $map = Join-Path ${env:UI-ATLAS_DATA_HOME} 'maps\synthetic-smoke.db'
  New-Item -ItemType Directory -Force -Path (Split-Path -Parent $recording),(Split-Path -Parent $map) | Out-Null
  Invoke-Cli synthetic-record --out $recording
  Invoke-Cli recording validate synthetic-smoke
  Invoke-Cli map build synthetic-smoke
  Invoke-Cli map validate synthetic-smoke
  Invoke-Cli map inspect synthetic-smoke world raw
  Invoke-Cli map inspect synthetic-smoke world semantic
  Invoke-Cli map quality synthetic-smoke
  Invoke-Cli list recordings
  Invoke-Cli list maps
  Invoke-Cli map export json synthetic-smoke
  $safeExport = Join-Path ${env:UI-ATLAS_DATA_HOME} 'exports\synthetic-smoke.json'
  Invoke-Cli export json validate $safeExport
  Invoke-Cli export $map --out (Join-Path $scratch 'graph.full.json') --include-sensitive-evidence --acknowledge-sensitive-evidence
  Invoke-CliExpectFailure export json delete (Join-Path $scratch 'graph.full.json') --yes
  Invoke-CliExpectFailure export map synthetic-smoke --out --acknowledge-sensitive-identities
  Invoke-Cli export map synthetic-smoke --acknowledge-sensitive-identities
  $humanExport = Join-Path ${env:UI-ATLAS_DATA_HOME} 'exports\synthetic-smoke-map.json'
  Invoke-Cli export map validate $humanExport
  $humanJson = Get-Content -LiteralPath $humanExport -Raw | ConvertFrom-Json
  if ($humanJson.formatVersion -cne 'ui-atlas.map.json/2' -or $null -eq $humanJson.rawDataStreams.windows -or
      $null -eq $humanJson.rawWorld.windows -or $null -eq $humanJson.semanticWorld.windows) { throw 'Human-readable export hierarchy is incomplete.' }
  Invoke-Cli export map synthetic-smoke format=ui-atlas_flat --project-id 'MAP' --acknowledge-sensitive-identities
  $UiAtlasExport = Join-Path ${env:UI-ATLAS_DATA_HOME} 'exports\synthetic-smoke-ui-atlas-flat.json'
  Invoke-Cli export map validate $UiAtlasExport
  $UiAtlasJson = Get-Content -LiteralPath $UiAtlasExport -Raw | ConvertFrom-Json
  if ($UiAtlasJson.projectId -cne 'MAP') { throw 'Case-sensitive export identity was not preserved.' }
  Invoke-Cli export map synthetic-smoke format=sqlite --acknowledge-sensitive-identities
  $sqliteExport = Join-Path ${env:UI-ATLAS_DATA_HOME} 'exports\synthetic-smoke-map.db'
  Invoke-Cli export map validate $sqliteExport
  $diffOutput = @(Invoke-Cli diff $map (Join-Path $scratch 'graph.full.json'))
  $diffOutput | Write-Output
  if (-not ($diffOutput -match '^nodes \+0 -0; edges \+0 -0$')) { throw 'Full graph round-trip was not lossless.' }
  Invoke-Consumer $safeExport
  Invoke-Cli export json delete $safeExport --yes
  Invoke-Cli export map delete $humanExport --yes
  Invoke-Cli export map delete $UiAtlasExport --yes
  Invoke-Cli export map delete $sqliteExport --yes
  Invoke-Cli map delete all --yes
  Invoke-Cli recording delete all --yes
  if (Test-Path -LiteralPath $map) { throw 'Map delete did not remove the catalog entry.' }
  if (Test-Path -LiteralPath $recording) { throw 'Recording delete did not remove the catalog entry.' }
  Write-Output 'Offline-style synthetic flow passed.'
}
finally {
  Remove-Item -LiteralPath 'Env:UI-ATLAS_DATA_HOME' -ErrorAction SilentlyContinue
  if ([IO.Path]::GetFullPath($scratch).StartsWith([IO.Path]::GetTempPath(), [StringComparison]::OrdinalIgnoreCase)) {
    Remove-Item -LiteralPath $scratch -Recurse -Force -ErrorAction SilentlyContinue
  }
}
