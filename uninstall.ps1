# Removes MachineGauges without prompts. (Settings > Apps > MachineGauges > Uninstall does the same.)
$exe = Join-Path $env:LOCALAPPDATA 'Programs\MachineGauges\MachineGauges.exe'
if (-not (Test-Path $exe)) { 'MachineGauges is not installed.'; return }

(Start-Process -FilePath $exe -ArgumentList '--uninstall', '--quiet' -PassThru).WaitForExit()
Start-Sleep -Seconds 3   # the install folder is deleted just after the exe exits
Unregister-ScheduledTask -TaskName 'MachineGauges Watchdog' -Confirm:$false -ErrorAction SilentlyContinue
'MachineGauges removed.'
