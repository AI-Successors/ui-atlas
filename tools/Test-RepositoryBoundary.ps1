param([string]$RepositoryRoot = (Split-Path -Parent $PSScriptRoot))
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath($RepositoryRoot).TrimEnd([IO.Path]::DirectorySeparatorChar)
if (-not (Test-Path -LiteralPath (Join-Path $root 'UiAtlas.Core.slnx'))) { throw 'Repository root marker is missing.' }

[xml]$solution = Get-Content -LiteralPath (Join-Path $root 'UiAtlas.Core.slnx')
$solution.SelectNodes('//Project') | ForEach-Object {
  $resolved = [IO.Path]::GetFullPath((Join-Path $root $_.Path))
  if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -or -not (Test-Path -LiteralPath $resolved)) {
    throw "Solution project escapes repository or is missing: $($_.Path)"
  }
}

$allowedPackages = @(
  'FirebirdSql.Data.FirebirdClient','Interop.UIAutomationClient','Microsoft.Data.Sqlite','Microsoft.Data.Sqlite.Core','SQLite','SQLitePCLRaw.bundle_e_sqlite3','SQLitePCLRaw.config.e_sqlite3',
  'SQLitePCLRaw.core','SQLitePCLRaw.provider.e_sqlite3','Microsoft.NET.Test.Sdk','Microsoft.CodeCoverage',
  'Microsoft.TestPlatform.ObjectModel','Microsoft.TestPlatform.TestHost','Newtonsoft.Json','xunit','xunit.abstractions',
  'xunit.analyzers','xunit.assert','xunit.core','xunit.extensibility.core','xunit.extensibility.execution','xunit.runner.visualstudio'
)

$approvedAssetPaths = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$ignoredSubtreePattern = '\\(artifacts|bin|obj|dist|\.codex-[^\\]+|\.tmp)\\'

function Resolve-ProjectIncludePaths {
  param(
    [string]$ProjectDirectory,
    [string]$Include
  )

  if ([string]::IsNullOrWhiteSpace($Include)) { return @() }

  if ($Include -notmatch '[*?]') {
    return ,([IO.Path]::GetFullPath((Join-Path $ProjectDirectory $Include)))
  }

  $relativeDirectory = Split-Path -Path $Include -Parent
  $pattern = Split-Path -Path $Include -Leaf
  $searchRoot = if ([string]::IsNullOrWhiteSpace($relativeDirectory) -or $relativeDirectory -eq '.') {
    $ProjectDirectory
  } else {
    [IO.Path]::GetFullPath((Join-Path $ProjectDirectory $relativeDirectory))
  }

  if (-not (Test-Path -LiteralPath $searchRoot -PathType Container)) {
    throw "Project input directory is missing: $Include"
  }

  $matches = @(Get-ChildItem -LiteralPath $searchRoot -Filter $pattern -File | ForEach-Object { [IO.Path]::GetFullPath($_.FullName) })
  if ($matches.Count -eq 0) {
    throw "Project input pattern matched nothing: $Include"
  }

  return $matches
}

Get-ChildItem -LiteralPath $root -Filter *.csproj -Recurse -File | Where-Object { $_.FullName -notmatch $ignoredSubtreePattern } | ForEach-Object {
  $projectFile = $_.FullName
  $projectDir = $_.DirectoryName
  [xml]$project = Get-Content -LiteralPath $projectFile
  if ($project.SelectNodes('//Import|//UsingTask|//Exec').Count -ne 0) { throw "Custom build hook is forbidden: $projectFile" }
  $project.Project.ItemGroup.ChildNodes | Where-Object { $_.Include } | ForEach-Object {
    if ($_.Name -eq 'PackageReference' -or $_.Include -like '*$(*') { return }
    if ($_.Link) { throw "Linked item is forbidden: $($_.Include)" }
    $resolvedPaths = @(Resolve-ProjectIncludePaths -ProjectDirectory $projectDir -Include $_.Include)
    foreach ($resolved in $resolvedPaths) {
      if (-not $resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Project input escapes repository: $($_.Include)"
      }
      if ($resolved -match '\.(png|jpg|jpeg|gif|ico|bmp|webp|svg)$') {
        $null = $approvedAssetPaths.Add($resolved)
      }
    }
  }
}

Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object { $_.FullName -notmatch $ignoredSubtreePattern -and $_.Extension -in '.props','.targets' } | ForEach-Object {
  [xml]$buildFile = Get-Content -LiteralPath $_.FullName
  if ($buildFile.SelectNodes('//Import|//UsingTask|//Exec').Count -ne 0) { throw "Repository build hook is forbidden: $($_.FullName)" }
}

$excludedNamespaceFragments = @('.Workflow', '.Execution', '.Broker', '.Scenario', '.Emulation')
Get-ChildItem -LiteralPath $root -Filter *.cs -Recurse -File | Where-Object { $_.FullName -notmatch $ignoredSubtreePattern } | ForEach-Object {
  $namespaceLines = @(Get-Content -LiteralPath $_.FullName | Where-Object { $_ -match '^\s*namespace\s+' })
  foreach ($fragment in $excludedNamespaceFragments) {
    if ($namespaceLines | Where-Object { $_.Contains($fragment) }) { throw "Excluded namespace family: $($_.FullName)" }
  }
}

Get-ChildItem -LiteralPath $root -Recurse -File | Where-Object {
  $_.FullName -notmatch $ignoredSubtreePattern -and $_.Extension -in '.png','.jpg','.jpeg','.gif','.ico','.bmp','.webp','.svg'
} | ForEach-Object {
  if (-not $approvedAssetPaths.Contains($_.FullName)) {
    throw "Unreviewed image asset is forbidden: $($_.FullName)"
  }
}

Get-ChildItem -LiteralPath $root -Filter packages.lock.json -Recurse -File | Where-Object { $_.FullName -notmatch $ignoredSubtreePattern } | ForEach-Object {
  $lock = Get-Content -LiteralPath $_.FullName -Raw | ConvertFrom-Json
  foreach ($frameworkProperty in $lock.dependencies.PSObject.Properties) {
    foreach ($packageProperty in $frameworkProperty.Value.PSObject.Properties) {
      if ($packageProperty.Value.type -eq 'Project') { continue }
      if ($packageProperty.Name -notin $allowedPackages) { throw "Package is outside the allowlist: $($packageProperty.Name)" }
    }
  }
}

Get-ChildItem -LiteralPath $root -Recurse -Force | Where-Object { $_.FullName -notmatch $ignoredSubtreePattern -and $_.Attributes -band [IO.FileAttributes]::ReparsePoint } | ForEach-Object {
  throw "Reparse point is forbidden: $($_.FullName.Substring($root.Length + 1))"
}

$networkReferences = @(
  ('Http' + 'Client'), ('Web' + 'Request'), ('Web' + 'Client'), ('System.Net.' + 'Sockets'),
  ('Tcp' + 'Client'), ('Udp' + 'Client'), ('Dns' + '.GetHost')
)
Get-ChildItem -LiteralPath $root -Filter *.cs -Recurse -File | Where-Object { $_.FullName -notmatch $ignoredSubtreePattern } | ForEach-Object {
  $text = Get-Content -LiteralPath $_.FullName -Raw
  foreach ($reference in $networkReferences) {
    if ($text.Contains($reference)) { throw "Network API reference: $($_.FullName.Substring($root.Length + 1))" }
  }
}

Write-Output 'Repository boundary checks passed.'
