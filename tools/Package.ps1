param([string]$Version = '2.0.0', [string]$Configuration = 'Release')
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$dist = Join-Path $root 'dist'
$staging = Join-Path $dist ('.staging-' + [Guid]::NewGuid().ToString('N'))
$payload = Join-Path $staging 'ui-atlas-core'
$buildRoot = Join-Path $staging 'build'
$isolatedBuild = "-p:UiAtlasIsolatedBuildRoot=$buildRoot"
$stableRevision = '-p:IncludeSourceRevisionInInformationalVersion=false'
New-Item -ItemType Directory -Force -Path $payload | Out-Null
try {
  dotnet restore (Join-Path $root 'UiAtlas.Core.slnx') --locked-mode $isolatedBuild $stableRevision
  if ($LASTEXITCODE) { throw 'Locked restore failed.' }
  dotnet build (Join-Path $root 'UiAtlas.Core.slnx') -c $Configuration --no-restore --no-incremental -p:DebugType=None -p:DebugSymbols=false -p:UseAppHost=false $isolatedBuild $stableRevision
  if ($LASTEXITCODE) { throw 'Deterministic package build failed.' }
  dotnet publish (Join-Path $root 'src\UiAtlas.Core.Cli\UiAtlas.Core.Cli.csproj') -c $Configuration --no-restore --no-build -p:PublishDir=$payload -p:DebugType=None -p:DebugSymbols=false -p:UseAppHost=false $isolatedBuild $stableRevision
  if ($LASTEXITCODE) { throw 'CLI publish failed.' }
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'ui-atlas.cmd') -Destination $payload
  Copy-Item -LiteralPath (Join-Path $PSScriptRoot 'Install-UiAtlas.ps1') -Destination (Join-Path $payload 'install.ps1')
  $desktop = Join-Path $staging 'desktop'
  dotnet publish (Join-Path $root 'src\UiAtlas.Core.Desktop\UiAtlas.Core.Desktop.csproj') -c $Configuration --no-restore --no-build -p:PublishDir=$desktop -p:DebugType=None -p:DebugSymbols=false -p:UseAppHost=false $isolatedBuild $stableRevision
  if ($LASTEXITCODE) { throw 'Desktop publish failed.' }
  Get-ChildItem -LiteralPath $desktop -Force | Copy-Item -Destination $payload -Recurse -Force
  $consumer = Join-Path $payload 'samples\consumer'
  dotnet publish (Join-Path $root 'samples\UiAtlas.Core.Consumer\UiAtlas.Core.Consumer.csproj') -c $Configuration --no-restore --no-build -p:PublishDir=$consumer -p:DebugType=None -p:DebugSymbols=false -p:UseAppHost=false $isolatedBuild $stableRevision
  if ($LASTEXITCODE) { throw 'Consumer sample publish failed.' }
  Copy-Item -LiteralPath (Join-Path $root 'README.md'),(Join-Path $root 'LICENSE'),(Join-Path $root 'THIRD-PARTY-NOTICES.md') -Destination $payload
  Copy-Item -LiteralPath (Join-Path $root 'schemas') -Destination $payload -Recurse
  Copy-Item -LiteralPath (Join-Path $root 'licenses') -Destination $payload -Recurse
  Copy-Item -LiteralPath (Join-Path $root 'sbom') -Destination $payload -Recurse
  Copy-Item -LiteralPath (Join-Path $root 'provenance') -Destination $payload -Recurse
  Copy-Item -LiteralPath (Join-Path $root 'docs') -Destination $payload -Recurse
  $utf8NoBom = [Text.UTF8Encoding]::new($false)
  Get-ChildItem -LiteralPath $payload -Recurse -File | Where-Object {
    $_.Extension -in '.json','.md','.txt','.ps1' -or $_.Name -in 'LICENSE','ui-atlas.cmd'
  } | ForEach-Object {
    $content = [IO.File]::ReadAllText($_.FullName).Replace("`r`n", "`n").Replace("`r", "`n")
    if ($_.Name -eq 'ui-atlas.cmd') { $content = $content.Replace("`n", "`r`n") }
    [IO.File]::WriteAllText($_.FullName, $content, $utf8NoBom)
  }
  Get-ChildItem -LiteralPath $payload -Filter *.pdb -Recurse -File | Remove-Item -Force
  Get-ChildItem -LiteralPath $payload -Directory -Recurse | Where-Object { $_.Parent.Name -eq 'runtimes' -and $_.Name -notlike 'win-*' } | ForEach-Object {
    $candidate = [IO.Path]::GetFullPath($_.FullName)
    if (-not $candidate.StartsWith($payload + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Runtime filter escaped package root.' }
    Remove-Item -LiteralPath $candidate -Recurse -Force
  }
  $packagedSbomPath = Join-Path $payload 'sbom\ui-atlas-core.cdx.json'
  $packagedSbom = Get-Content -LiteralPath $packagedSbomPath -Raw | ConvertFrom-Json
  $oldRootRef = $packagedSbom.metadata.component.'bom-ref'
  $newRootRef = "pkg:generic/ui-atlas.core@$Version"
  $packagedSbom.metadata.component.version = $Version
  $packagedSbom.metadata.component.'bom-ref' = $newRootRef
  foreach ($dependency in $packagedSbom.dependencies) { if ($dependency.ref -eq $oldRootRef) { $dependency.ref = $newRootRef } }
  $packagedSbom | ConvertTo-Json -Depth 100 | Set-Content -LiteralPath $packagedSbomPath -Encoding utf8
  $deterministicTimestamp = [DateTime]::SpecifyKind([DateTime]::new(1980,1,1,0,0,0), [DateTimeKind]::Utc)
  Get-ChildItem -LiteralPath $payload -Recurse -File | ForEach-Object { $_.LastWriteTimeUtc = $deterministicTimestamp }

  $zipPath = Join-Path $dist "ui-atlas-core-$Version-windows.zip"
  if (Test-Path -LiteralPath $zipPath) { throw "Release archive already exists: $zipPath" }
  Add-Type -AssemblyName System.IO.Compression
  $stream = [IO.File]::Open($zipPath, [IO.FileMode]::CreateNew)
  try {
    $archive = [IO.Compression.ZipArchive]::new($stream, [IO.Compression.ZipArchiveMode]::Create, $false)
    try {
      Get-ChildItem -LiteralPath $payload -Recurse -File | Sort-Object FullName | ForEach-Object {
        $entryPath = [IO.Path]::GetFullPath($_.FullName)
        $stagingPrefix = $staging.TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
        if (-not $entryPath.StartsWith($stagingPrefix, [StringComparison]::OrdinalIgnoreCase)) { throw 'Package entry escaped staging root.' }
        $entryName = $entryPath.Substring($stagingPrefix.Length).Replace('\','/')
        $entry = $archive.CreateEntry($entryName, [IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime = [DateTimeOffset]::new(1980,1,1,0,0,0,[TimeSpan]::Zero)
        $input = [IO.File]::OpenRead($_.FullName)
        $output = $entry.Open()
        try { $input.CopyTo($output) } finally { $output.Dispose(); $input.Dispose() }
      }
    } finally { $archive.Dispose() }
  } finally { $stream.Dispose() }
  & (Join-Path $PSScriptRoot 'Test-Package.ps1') -Archive $zipPath
  if ($LASTEXITCODE) { throw 'Package compliance checks failed.' }
  $hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
  Set-Content -LiteralPath (Join-Path $dist "ui-atlas-core-$Version-windows.sha256") -Value "$hash  $(Split-Path -Leaf $zipPath)" -Encoding ascii -NoNewline
  Write-Output $zipPath
}
finally {
  if (Test-Path -LiteralPath $staging) {
    $resolved = [IO.Path]::GetFullPath($staging)
    if ($resolved.StartsWith($dist + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
      Remove-Item -LiteralPath $resolved -Recurse -Force
    }
  }
}
