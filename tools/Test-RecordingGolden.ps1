param(
  [Parameter(Mandatory = $true)] [string]$Recording,
  [string]$Baseline = '',
  [switch]$Update,
  [int[]]$ReviewedFrames = @(34, 35, 38),
  [string]$Configuration = 'Release'
)

$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot
$resolvedBaseline = if ([string]::IsNullOrWhiteSpace($Baseline)) {
  Join-Path $repositoryRoot 'tests\golden\excel-reference.json'
} else {
  $Baseline
}
$recordingPath = [IO.Path]::GetFullPath($Recording)
$baselinePath = [IO.Path]::GetFullPath($resolvedBaseline)
if (-not (Test-Path -LiteralPath $recordingPath -PathType Leaf)) {
  throw "Recording does not exist: $recordingPath"
}

$recordingId = [IO.Path]::GetFileNameWithoutExtension($recordingPath)
$cliExe = Join-Path $repositoryRoot "src\UiAtlas.Core.Cli\bin\$Configuration\net10.0-windows10.0.19041.0\ui-atlas.exe"
$cliDll = Join-Path $repositoryRoot "src\UiAtlas.Core.Cli\bin\$Configuration\net10.0-windows10.0.19041.0\ui-atlas.dll"
if (-not (Test-Path -LiteralPath $cliExe) -and -not (Test-Path -LiteralPath $cliDll)) {
  throw 'Build UiAtlas.Core.Cli before running the golden recording check.'
}

function Invoke-UiAtlas([Parameter(ValueFromRemainingArguments)] [string[]]$Arguments) {
  if (Test-Path -LiteralPath $cliExe) { & $cliExe @Arguments }
  else { & dotnet $cliDll @Arguments }
  if ($LASTEXITCODE) { throw "UiAtlas command failed: $($Arguments -join ' ')" }
}

function Get-ControlProperty($Control, [string]$Name) {
  $property = $Control.properties.PSObject.Properties[$Name]
  if ($null -eq $property) { return '' }
  if ($property.Value -is [Array]) { return (@($property.Value) | Sort-Object) -join ',' }
  return [string]$property.Value
}

function Get-Sha256Text([string]$Value) {
  $bytes = [Text.Encoding]::UTF8.GetBytes($Value)
  $algorithm = [Security.Cryptography.SHA256]::Create()
  try {
    $hash = $algorithm.ComputeHash($bytes)
    return ([BitConverter]::ToString($hash) -replace '-', '').ToLowerInvariant()
  }
  finally { $algorithm.Dispose() }
}

function Get-CountMap([object[]]$Items, [scriptblock]$KeySelector) {
  $result = [ordered]@{}
  foreach ($group in @($Items | Group-Object -Property $KeySelector | Sort-Object Name)) {
    $name = if ([string]::IsNullOrWhiteSpace($group.Name)) { '(none)' } else { $group.Name }
    $result[$name] = $group.Count
  }
  return $result
}

function Read-CaptureSummary([string]$Path) {
  Add-Type -AssemblyName System.IO.Compression.FileSystem
  $archive = [IO.Compression.ZipFile]::OpenRead($Path)
  try {
    $statuses = @()
    foreach ($entry in @($archive.Entries | Where-Object { $_.FullName -like 'raw/observations/frame-*.json' })) {
      $reader = [IO.StreamReader]::new($entry.Open(), [Text.Encoding]::UTF8, $true, 1024, $false)
      try { $statuses += ($reader.ReadToEnd() | ConvertFrom-Json).automationStatus }
      finally { $reader.Dispose() }
    }

    $fallbackReasons = @()
    $healthEntry = $archive.GetEntry('raw/capture-health.jsonl')
    if ($null -ne $healthEntry) {
      $reader = [IO.StreamReader]::new($healthEntry.Open(), [Text.Encoding]::UTF8, $true, 1024, $false)
      try {
        while (-not $reader.EndOfStream) {
          $line = $reader.ReadLine()
          if ([string]::IsNullOrWhiteSpace($line)) { continue }
          $health = $line | ConvertFrom-Json
          if ($health.status -ne 'popup-visual-fallback') { continue }
          if ($health.detail -match '\(([^)]+)\)') { $fallbackReasons += $Matches[1] }
          else { $fallbackReasons += 'unspecified' }
        }
      }
      finally { $reader.Dispose() }
    }

    return [ordered]@{
      automationStatusCounts = Get-CountMap $statuses { $_ }
      popupFallbackReasonCounts = Get-CountMap $fallbackReasons { $_ }
    }
  }
  finally { $archive.Dispose() }
}

function New-GoldenSnapshot($Graph, [string]$Path) {
  $variants = @($Graph.rawDataStreams.windows | ForEach-Object { $_.variants })
  $frames = foreach ($variant in @($variants | Sort-Object frameSequence)) {
    $controls = @($variant.controls)
    $canonicalControls = foreach ($control in $controls) {
      $bounds = $control.bounds
      $patterns = Get-ControlProperty $control 'supportedPattern'
      $role = Get-ControlProperty $control 'visualRole'
      $row = Get-ControlProperty $control 'tableRow'
      $column = Get-ControlProperty $control 'tableColumn'
      "$($control.kind)|$($control.name)|$($bounds.x),$($bounds.y),$($bounds.width),$($bounds.height)|$role|$row|$column|$patterns"
    }
    $canonical = (@($canonicalControls | Sort-Object) -join "`n")
    [ordered]@{
      sequence = [int]$variant.frameSequence
      reviewed = [bool]($ReviewedFrames -contains [int]$variant.frameSequence)
      controlCount = $controls.Count
      digest = Get-Sha256Text $canonical
      typeCounts = Get-CountMap $controls { $_.kind }
      roleCounts = Get-CountMap $controls { Get-ControlProperty $_ 'visualRole' }
    }
  }

  return [ordered]@{
    formatVersion = 'ui-atlas.recording-golden/1'
    recordingId = $recordingId
    recordingSha256 = (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
    frameCount = $frames.Count
    capture = Read-CaptureSummary $Path
    frames = @($frames)
  }
}

$scratch = Join-Path ([IO.Path]::GetTempPath()) ('ui-atlas-golden-' + [Guid]::NewGuid().ToString('N'))
$oldDataHome = [Environment]::GetEnvironmentVariable('UI-ATLAS_DATA_HOME', 'Process')
New-Item -ItemType Directory -Path (Join-Path $scratch 'recordings') -Force | Out-Null
try {
  Copy-Item -LiteralPath $recordingPath -Destination (Join-Path $scratch "recordings\$recordingId.mlrec")
  [Environment]::SetEnvironmentVariable('UI-ATLAS_DATA_HOME', $scratch, 'Process')
  Invoke-UiAtlas map build $recordingId
  $exportPath = Join-Path $scratch 'golden-map.json'
  Invoke-UiAtlas export map $recordingId format=json --out $exportPath --acknowledge-sensitive-identities
  $graph = Get-Content -Raw -LiteralPath $exportPath | ConvertFrom-Json
  $actual = New-GoldenSnapshot $graph $recordingPath

  if ($Update) {
    New-Item -ItemType Directory -Path (Split-Path -Parent $baselinePath) -Force | Out-Null
    $actual | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath $baselinePath -Encoding utf8
    Write-Output "Golden baseline updated: $baselinePath"
  }
  else {
    if (-not (Test-Path -LiteralPath $baselinePath -PathType Leaf)) {
      throw "Golden baseline does not exist: $baselinePath"
    }
    $expected = Get-Content -Raw -LiteralPath $baselinePath | ConvertFrom-Json
    if ($expected.formatVersion -ne 'ui-atlas.recording-golden/1') { throw 'Unsupported golden baseline format.' }
    if ($expected.recordingSha256 -ne $actual.recordingSha256) { throw 'The supplied recording is not the golden source recording.' }

    $differences = @()
    $actualBySequence = @{}
    foreach ($frame in $actual.frames) {
      $actualBySequence[[int]$frame.sequence] = $frame
    }
    foreach ($frame in $expected.frames) {
      $candidate = $actualBySequence[[int]$frame.sequence]
      if ($null -eq $candidate) {
        $differences += "Frame $($frame.sequence) is missing."
      }
      elseif ($frame.controlCount -ne $candidate.controlCount -or $frame.digest -ne $candidate.digest) {
        $review = if ($frame.reviewed) { 'reviewed' } else { 'provisional' }
        $differences += "Frame $($frame.sequence) changed ($review): controls $($frame.controlCount) -> $($candidate.controlCount)."
      }
    }
    foreach ($frame in $actual.frames) {
      if (-not @($expected.frames.sequence).Contains([int]$frame.sequence)) {
        $differences += "Unexpected frame $($frame.sequence) was added."
      }
    }
    if ($differences.Count -gt 0) { throw ($differences -join [Environment]::NewLine) }
    Write-Output "Golden recording passed: $($actual.frameCount) frames; $(@($actual.frames | Where-Object reviewed).Count) reviewed."
  }

  $fallback = $actual.capture.popupFallbackReasonCounts
  $fallbackText = @($fallback.Keys | ForEach-Object { "$_=$($fallback[$_])" }) -join ', '
  Write-Output "Popup accessibility fallback reasons: $fallbackText"
}
finally {
  [Environment]::SetEnvironmentVariable('UI-ATLAS_DATA_HOME', $oldDataHome, 'Process')
  if (Test-Path -LiteralPath $scratch) { Remove-Item -LiteralPath $scratch -Recurse -Force }
}
