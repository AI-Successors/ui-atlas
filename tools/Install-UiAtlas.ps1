param(
  [string]$InstallRoot = (Join-Path $env:LOCALAPPDATA 'Programs\UiAtlas'),
  [switch]$NoShortcut
)

$ErrorActionPreference = 'Stop'
$sourceRoot = [IO.Path]::GetFullPath($PSScriptRoot)
$destination = [IO.Path]::GetFullPath($InstallRoot)
$destinationRoot = [IO.Path]::GetPathRoot($destination)
if ([string]::IsNullOrWhiteSpace($destination) -or $destination -eq $destinationRoot) {
  throw 'InstallRoot must be a dedicated application directory, not a drive root.'
}
if (-not (Test-Path -LiteralPath (Join-Path $sourceRoot 'ui-atlas.dll'))) {
  throw 'Run this script from the extracted UiAtlas release folder.'
}

$runtime = & dotnet --list-runtimes 2>$null | Where-Object { $_ -match '^Microsoft\.WindowsDesktop\.App 10\.' } | Select-Object -First 1
if (-not $runtime) {
  throw 'UiAtlas requires the Microsoft Windows Desktop Runtime 10.x.'
}

if ($sourceRoot.TrimEnd('\') -eq $destination.TrimEnd('\')) {
  Write-Output "UiAtlas is already running from $destination"
  return
}

$parent = Split-Path -Parent $destination
New-Item -ItemType Directory -Path $parent -Force | Out-Null
$staging = Join-Path $parent ('.ui-atlas-install-' + [Guid]::NewGuid().ToString('N'))
$backup = $null
try {
  New-Item -ItemType Directory -Path $staging | Out-Null
  Get-ChildItem -LiteralPath $sourceRoot -Force | Copy-Item -Destination $staging -Recurse -Force

  if (Test-Path -LiteralPath $destination) {
    $backup = $destination + '.backup-' + [DateTimeOffset]::UtcNow.ToString('yyyyMMddHHmmss') + '-' + [Guid]::NewGuid().ToString('N').Substring(0, 8)
    Move-Item -LiteralPath $destination -Destination $backup
  }
  Move-Item -LiteralPath $staging -Destination $destination

  if (-not $NoShortcut) {
    $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
    New-Item -ItemType Directory -Path $startMenu -Force | Out-Null
    $shortcutPath = Join-Path $startMenu 'UiAtlas Recorder.lnk'
    $shell = New-Object -ComObject WScript.Shell
    $shortcut = $shell.CreateShortcut($shortcutPath)
    $shortcut.TargetPath = (Join-Path $destination 'ui-atlas.cmd')
    $shortcut.WorkingDirectory = $destination
    $shortcut.Description = 'Map legacy Windows interfaces with UiAtlas'
    $shortcut.Save()
  }

  Write-Output "UiAtlas installed to $destination"
  Write-Output "Start it with: $destination\ui-atlas.cmd"
  if ($backup) { Write-Output "Previous installation kept at $backup" }
}
catch {
  if (-not (Test-Path -LiteralPath $destination) -and $backup -and (Test-Path -LiteralPath $backup)) {
    Move-Item -LiteralPath $backup -Destination $destination
  }
  throw
}
finally {
  if (Test-Path -LiteralPath $staging) {
    $resolvedStaging = [IO.Path]::GetFullPath($staging)
    $safePrefix = [IO.Path]::GetFullPath($parent).TrimEnd('\') + '\'
    if ($resolvedStaging.StartsWith($safePrefix, [StringComparison]::OrdinalIgnoreCase)) {
      Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
  }
}
