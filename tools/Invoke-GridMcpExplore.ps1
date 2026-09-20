param(
  [Parameter(Mandatory)][string]$Executable,
  [Parameter(Mandatory)][string]$CatalogRoot,
  [Parameter(Mandatory)][string]$OutputDirectory,
  [string]$AppProcessName,
  [string]$AppWindowTitle,
  [string]$ReadGridName,
  [int]$CancelAfterMs = 0
)
$ErrorActionPreference = 'Stop'
if ($PSVersionTable.PSVersion.Major -lt 7) { throw 'Use PowerShell 7 (pwsh).' }
if ($AppWindowTitle -and -not $AppProcessName) { throw 'AppWindowTitle requires AppProcessName. Omit both for listing only.' }
$executablePath = (Resolve-Path -LiteralPath $Executable).Path
$catalogPath = [IO.Path]::GetFullPath($CatalogRoot)
$outputPath = [IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Force -Path $outputPath | Out-Null
$start = [Diagnostics.ProcessStartInfo]::new($executablePath)
$start.UseShellExecute = $false
$start.CreateNoWindow = $true
$start.RedirectStandardInput = $true
$start.RedirectStandardOutput = $true
$start.RedirectStandardError = $true
$start.ArgumentList.Add('--catalog-root')
$start.ArgumentList.Add($catalogPath)
$process = [Diagnostics.Process]::Start($start)
$process.StandardInput.AutoFlush = $true
$stderr = $process.StandardError.ReadToEndAsync()
$demoRequestNumber = 0
$transcript = [Collections.Generic.List[object]]::new()

# This fixed rehearsal client invokes public MCP tools only. It never loads expected answers or controls the UI itself.
function Request([string]$Method, $Parameters) {
  $script:demoRequestNumber++
  $id = $script:demoRequestNumber
  $request = @{ jsonrpc='2.0'; id=$id; method=$Method; params=$Parameters }
  $watch = [Diagnostics.Stopwatch]::StartNew()
  $process.StandardInput.WriteLine(($request | ConvertTo-Json -Depth 50 -Compress))
  while ($true) {
    $line = $process.StandardOutput.ReadLineAsync().WaitAsync([TimeSpan]::FromSeconds(35)).GetAwaiter().GetResult()
    if ($null -eq $line) { throw 'MCP host disconnected before its response.' }
    $response = $line | ConvertFrom-Json -Depth 100
    if ($response.id -eq $id) {
      $recorded = $response | ConvertTo-Json -Depth 100 | ConvertFrom-Json -Depth 100
      foreach ($block in @($recorded.result.content)) { if ($block.type -eq 'image') { $block.data = '[image saved separately]' } }
      $transcript.Add([ordered]@{ method=$Method; request=$Parameters; elapsedMs=$watch.ElapsedMilliseconds; response=$recorded })
      if ($response.error) { throw ('MCP protocol error: '+($response.error | ConvertTo-Json -Compress)) }
      return $response
    }
    if ($null -ne $response.id) { throw 'Unexpected server request; this rehearsal client grants no additional capabilities.' }
  }
}
function Tool([string]$Name, $Arguments) {
  $response = Request 'tools/call' @{ name=$Name; arguments=$Arguments }
  if ($response.result.isError) { throw ('Tool error: '+($response.result | ConvertTo-Json -Depth 20 -Compress)) }
  if ($response.result.structuredContent) { return $response.result.structuredContent }
  return $response.result.content[0].text | ConvertFrom-Json -Depth 100
}
function Save-Json([string]$Name, $Value) {
  [IO.File]::WriteAllText((Join-Path $outputPath $Name), ($Value | ConvertTo-Json -Depth 100), [Text.UTF8Encoding]::new($false))
}
try {
  $initialize = Request 'initialize' @{ protocolVersion='2025-11-25'; capabilities=@{}; clientInfo=@{name='ui-atlas-demo';version='0.1.0'} }
  $process.StandardInput.WriteLine('{"jsonrpc":"2.0","method":"notifications/initialized"}')
  Save-Json 'initialize.json' $initialize
  Save-Json 'tools.json' (Request 'tools/list' @{})
  $catalog = Tool 'list_apps' @{}
  Save-Json 'apps.json' $catalog
  if ($AppProcessName) {
    $selected = @($catalog.apps | Where-Object { $_.processName -ieq $AppProcessName -and (-not $AppWindowTitle -or $_.windowTitle -ceq $AppWindowTitle) })
    if ($selected.Count -ne 1) { throw 'Select exactly one listed app. Use AppWindowTitle to distinguish windows.' }
    $arguments = @{ appId=$selected[0].appId; requestId=[Guid]::NewGuid().ToString('N') }
    $startTool = 'show_app_grids'
    $pollTool = 'get_grid_exploration'
    if ($ReadGridName) {
      $grids = Tool 'list_app_grids' @{appId=$selected[0].appId}
      Save-Json 'grids.json' $grids
      $grid = @($grids.grids | Where-Object { $_.name -ieq $ReadGridName -and $_.available })
      if ($grid.Count -ne 1) { throw 'Exactly one matching available grid is required. Inspect grids.json for availability reasons.' }
      $arguments.gridRef = $grid[0].gridRef
      $startTool = 'start_grid_read'
      $pollTool = 'get_grid_read'
    }
    $operation = Tool $startTool $arguments
    Save-Json 'start.json' $operation
    if (-not $operation.ok) { throw ($operation.error | ConvertTo-Json -Compress) }
    $id = $operation.operation.acquisitionId
    $repeat = Tool $startTool $arguments
    if ($repeat.operation.acquisitionId -ne $id) { throw 'Identical request unexpectedly changed acquisition ID.' }
    Write-Output "Waiting for your local UI Atlas HUD approval. The client never clicks approval."
    $watch = [Diagnostics.Stopwatch]::StartNew()
    $activeWatch = $null
    $lastPhase = ''
    $cancelled = $false
    while ($operation.operation.progress.lifecycle -ine 'Finished') {
      if ($operation.operation.progress.lifecycle -eq 'Running' -and $null -eq $activeWatch) { $activeWatch = [Diagnostics.Stopwatch]::StartNew() }
      if ($null -ne $activeWatch -and $activeWatch.Elapsed.TotalMinutes -gt 6) { [void](Tool 'cancel_grid_exploration' @{acquisitionId=$id}); throw 'Active read deadline exceeded; cancellation requested.' }
      if ($CancelAfterMs -gt 0 -and -not $cancelled -and $watch.ElapsedMilliseconds -ge $CancelAfterMs) {
        $operation = Tool 'cancel_grid_exploration' @{acquisitionId=$id}; $cancelled=$true
      }
      Start-Sleep -Milliseconds 1000
      $operation = Tool $pollTool @{acquisitionId=$id}
      if (-not $operation.ok) { throw ($operation.error | ConvertTo-Json -Compress) }
      $phase = $operation.operation.progress.lifecycle + '/' + $operation.operation.progress.stage
      if ($phase -ne $lastPhase) { Write-Output ($phase + ': ' + $operation.operation.progress.reason); $lastPhase = $phase }
    }
    $imageResponse = Request 'tools/call' @{ name='get_grid_exploration'; arguments=@{acquisitionId=$id;includeImage=$true} }
    foreach ($block in @($imageResponse.result.content)) {
      if ($block.type -eq 'image') {
        $bytes = [Convert]::FromBase64String($block.data)
        [IO.File]::WriteAllBytes((Join-Path $outputPath 'table.png'), $bytes)
        $actualHash = (Get-FileHash -LiteralPath (Join-Path $outputPath 'table.png')).Hash.ToLowerInvariant()
        if ($actualHash -ne $operation.operation.result.imageSha256) { throw 'MCP image hash mismatch.' }
      }
    }
    Save-Json 'result.json' $operation
    if ($ReadGridName -and $operation.status -eq 'Complete') {
      $export = Tool 'export_grid_to_excel' @{datasetId=$operation.datasetId}
      Save-Json 'export.json' $export
      Write-Output ("Excel saved: {0}; rows={1}; columns={2}; verified={3}" -f $export.file.path,$export.file.rowCount,$export.file.columnCount,$export.file.verified)
    } elseif ($ReadGridName) { Write-Output ("Dataset {0}: {1}. No partial or failed export was requested." -f $operation.status,($operation.reasons -join ';')) }
    $result = $operation.operation.result
    # Retain original evidence for this run before the host result lifetime ends on disconnect.
    if ($result.captureManifestPath) {
      $source = [IO.Path]::GetFullPath((Split-Path -Parent $result.captureManifestPath))
      $allowed = [IO.Path]::GetFullPath((Join-Path $catalogPath 'grid-reads')) + [IO.Path]::DirectorySeparatorChar
      if (-not $source.StartsWith($allowed,[StringComparison]::OrdinalIgnoreCase)) { throw 'Evidence source is outside the selected catalog.' }
      $entries = @(Get-ChildItem -LiteralPath $source -Recurse -Force)
      if ($entries | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }) { throw 'Linked evidence is not copied.' }
      if ((Get-Item -LiteralPath $source).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked evidence root is not copied.' }
      Copy-Item -LiteralPath $source -Destination (Join-Path $outputPath 'evidence') -Recurse
    }
    Write-Output ("{0} ({1}): {2} captures, {3} movements; columnsComplete={4}; rowsComplete={5}; restoration={6}; {7} ms; image={8}; reasons={9}" -f $result.status,$result.scope,$result.tileCount,$result.movementCount,$result.coverage.columnCoverageComplete,$result.coverage.rowCoverageComplete,$result.restoration.status,$watch.ElapsedMilliseconds,(Test-Path -LiteralPath (Join-Path $outputPath 'table.png')),($result.reasons -join ';'))
  } else { Write-Output ("Listed {0} running apps; listingComplete={1}. No acquisition started." -f @($catalog.apps).Count,$catalog.listingComplete) }
}
finally {
  Save-Json 'transcript.json' $transcript
  Save-Json 'binary.json' @{ executable=$executablePath; sha256=(Get-FileHash -LiteralPath $executablePath -Algorithm SHA256).Hash; processId=$process.Id }
  $process.StandardInput.Close()
  if (-not $process.WaitForExit(15000)) { throw 'Host has not finished its safe shutdown; inspect the process before ending it.' }
  [IO.File]::WriteAllText((Join-Path $outputPath 'stderr.txt'), $stderr.GetAwaiter().GetResult())
  $process.Dispose()
}
