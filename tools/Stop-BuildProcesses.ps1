param(
  [Parameter(Mandatory)]
  [ValidateNotNullOrEmpty()]
  [string]$TargetPath
)

$ErrorActionPreference = 'Stop'
if (-not [IO.Path]::IsPathRooted($TargetPath) -or [IO.Path]::GetExtension($TargetPath) -ne '.dll') {
  throw 'An absolute build target DLL path is required.'
}

# Match actual build outputs, including the adapter copied beside the CLI.
# Never stop by process name alone or kill arbitrary descendants of the recorder.
$assemblies = @([IO.Path]::GetFullPath($TargetPath))
if ([IO.Path]::GetFileName($TargetPath) -eq 'ui-atlas.dll') {
  $assemblies += Join-Path (Split-Path -Parent $assemblies[0]) 'migration-firebird\ui-atlas-migrate-firebird.dll'
}
$executables = @($assemblies | ForEach-Object { [IO.Path]::ChangeExtension($_, '.exe') })
$names = @($assemblies | ForEach-Object { [IO.Path]::GetFileNameWithoutExtension($_) }) + 'dotnet'

# A second snapshot catches helpers started while their parent was being stopped.
for ($attempt = 0; $attempt -lt 3; $attempt++) {
  $stopped = 0
  foreach ($process in @(Get-Process -Name $names -ErrorAction SilentlyContinue)) {
    try {
      if ($process.Id -eq $PID) { continue }
      $matchesOutput = $false
      try {
        # Retain the process handle through inspection and termination, including
        # when another project build stops the same helper concurrently.
        $null = $process.Handle
        if ($process.HasExited) { continue }
        $matchesOutput = $process.Path -in $executables
        if (-not $matchesOutput -and $process.ProcessName -eq 'dotnet') {
          # Loaded module paths also identify relative-path and `dotnet exec`
          # launches without guessing a process's working directory or arguments.
          $matchesOutput = @($process.Modules | Where-Object { $_.FileName -in $assemblies }).Count -gt 0
        }
      }
      catch {
        # Uninspectable processes cannot be proven to own this build output.
        Write-Verbose "Could not inspect process $($process.Id): $_"
        continue
      }
      if (-not $matchesOutput) { continue }

      Write-Output "Stopping build output process $($process.ProcessName) (PID $($process.Id))."
      try {
        $process.Kill()
        if (-not $process.WaitForExit(5000)) { throw 'Process did not exit within five seconds.' }
      }
      catch {
        if (-not $process.HasExited) {
          throw "Could not stop build output process $($process.Id). Close it before building. $_"
        }
      }
      $stopped++
    }
    finally {
      $process.Dispose()
    }
  }
  if ($stopped -eq 0) { return }
}
throw 'Build output processes keep restarting. Stop their launcher before building.'
