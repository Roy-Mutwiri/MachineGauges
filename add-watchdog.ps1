# Optional: adds a scheduled task that restarts MachineGauges within 5 minutes if it
# ever stops. Run this one from an ADMIN PowerShell window - task creation needs it.
$ErrorActionPreference = 'Stop'

$exe = Join-Path $env:LOCALAPPDATA 'Programs\MachineGauges\MachineGauges.exe'
if (-not (Test-Path $exe)) { throw "MachineGauges is not installed at $exe" }

# --autostart keeps it silent: a launch while it is already running just exits.
$action   = New-ScheduledTaskAction -Execute $exe -Argument '--autostart'
$atLogon  = New-ScheduledTaskTrigger -AtLogOn
$repeat   = New-ScheduledTaskTrigger -Once -At (Get-Date).AddMinutes(2) `
                -RepetitionInterval (New-TimeSpan -Minutes 5)
$settings = New-ScheduledTaskSettingsSet -AllowStartIfOnBatteries -DontStopIfGoingOnBatteries `
                -StartWhenAvailable -MultipleInstances IgnoreNew -ExecutionTimeLimit ([TimeSpan]::Zero)

Register-ScheduledTask -TaskName 'MachineGauges Watchdog' -Action $action `
    -Trigger $atLogon, $repeat -Settings $settings -Force | Out-Null

'Watchdog installed: MachineGauges is relaunched within 5 minutes if it ever stops.'
