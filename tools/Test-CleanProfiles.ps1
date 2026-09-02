param(
  [Parameter(Mandatory=$true)][string]$Archive,
  [ValidateRange(1, 20)][int]$Profiles = 10
)

$ErrorActionPreference = 'Stop'
$archivePath = [IO.Path]::GetFullPath($Archive)
if (-not (Test-Path -LiteralPath $archivePath)) { throw "Release archive does not exist: $archivePath" }
$scratch = Join-Path ([IO.Path]::GetTempPath()) ('ui-atlas-clean-profiles-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $scratch | Out-Null
$priorDataHome = ${env:UI-ATLAS_DATA_HOME}

function Invoke-UiAtlas([string]$CliDll, [Parameter(ValueFromRemainingArguments)][string[]]$Arguments) {
  & dotnet $CliDll @Arguments
  if ($LASTEXITCODE) { throw "UiAtlas command failed: $($Arguments -join ' ')" }
}

try {
  for ($index = 1; $index -le $Profiles; $index++) {
    $profileRoot = Join-Path $scratch ('profile-{0:D2}' -f $index)
    $extracted = Join-Path $profileRoot 'extracted'
    $installed = Join-Path $profileRoot 'installed'
    ${env:UI-ATLAS_DATA_HOME} = Join-Path $profileRoot 'data'
    Expand-Archive -LiteralPath $archivePath -DestinationPath $extracted
    $packageRoot = Join-Path $extracted 'ui-atlas-core'
    & (Join-Path $packageRoot 'install.ps1') -InstallRoot $installed -NoShortcut
    if ($LASTEXITCODE) { throw "Clean-profile install $index failed." }

    $cli = Join-Path $installed 'ui-atlas.dll'
    $recording = Join-Path ${env:UI-ATLAS_DATA_HOME} 'recordings\clean-profile.mlrec'
    New-Item -ItemType Directory -Path (Split-Path -Parent $recording) -Force | Out-Null
    Invoke-UiAtlas $cli help
    Invoke-UiAtlas $cli synthetic-record --out $recording
    Invoke-UiAtlas $cli recording validate clean-profile
    Invoke-UiAtlas $cli map build clean-profile
    Invoke-UiAtlas $cli map validate clean-profile
    Invoke-UiAtlas $cli map quality clean-profile --strict
    Invoke-UiAtlas $cli map export json clean-profile
    $consumer = Join-Path $installed 'samples\consumer\UiAtlas.Core.Consumer.dll'
    Invoke-UiAtlas $consumer (Join-Path ${env:UI-ATLAS_DATA_HOME} 'maps\clean-profile.db')
    Invoke-UiAtlas $cli map delete clean-profile --yes
    Invoke-UiAtlas $cli recording delete clean-profile --yes
    if ((Get-ChildItem -LiteralPath (Join-Path ${env:UI-ATLAS_DATA_HOME} 'maps') -Filter *.db -File -ErrorAction SilentlyContinue).Count -ne 0) {
      throw "Clean profile $index retained an active map after deletion."
    }
    Write-Output "Clean profile $index/$Profiles passed."
  }
  Write-Output "$Profiles/$Profiles isolated clean-profile installations passed on this Windows host."
}
finally {
  if ($null -eq $priorDataHome) { Remove-Item -LiteralPath 'Env:UI-ATLAS_DATA_HOME' -ErrorAction SilentlyContinue }
  else { ${env:UI-ATLAS_DATA_HOME} = $priorDataHome }
  if (Test-Path -LiteralPath $scratch) {
    $resolvedScratch = [IO.Path]::GetFullPath($scratch)
    $safePrefix = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if ($resolvedScratch.StartsWith($safePrefix, [StringComparison]::OrdinalIgnoreCase)) {
      Remove-Item -LiteralPath $resolvedScratch -Recurse -Force
    }
  }
}
