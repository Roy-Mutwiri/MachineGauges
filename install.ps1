# Installs (or updates) MachineGauges for the current user from a local build.
# Downloaded copies don't need this: just double-click MachineGauges.exe.
$ErrorActionPreference = 'Stop'

$source = Join-Path $PSScriptRoot 'build\MachineGauges.exe'
if (-not (Test-Path $source)) { throw "Build output not found: $source. Run build.ps1 first." }

# Wait for the installer process only: -Wait would also wait on the overlay it launches.
(Start-Process -FilePath $source -ArgumentList '--install', '--quiet' -PassThru).WaitForExit()
Start-Sleep -Seconds 2

$exe  = Join-Path $env:LOCALAPPDATA 'Programs\MachineGauges\MachineGauges.exe'
$proc = Get-Process -Name MachineGauges -ErrorAction SilentlyContinue
if (-not (Test-Path $exe)) { throw 'Install failed: the exe was not copied.' }
if (-not $proc) { throw 'Installed, but MachineGauges is not running.' }

"Installed : $exe"
"Version   : $((Get-Item $exe).VersionInfo.FileVersion)"
"Running   : PID $($proc.Id)"
