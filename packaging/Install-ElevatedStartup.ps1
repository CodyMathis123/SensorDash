# Installs an elevated per-user logon task after one UAC approval.
$ErrorActionPreference = 'Stop'
$taskName = 'SensorDash (Elevated)'
$exe = Join-Path $PSScriptRoot 'SensorDash.exe'
if (-not (Test-Path -LiteralPath $exe)) { throw 'SensorDash.exe is missing. Extract the whole release first.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Requesting administrator permission to create an elevated logon task...'
    $argsToPass = '-NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $PSCommandPath.Replace('"','')
    $child = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argsToPass -Wait -PassThru
    if ($child.ExitCode -ne 0) { throw "Administrator installer failed with exit code $($child.ExitCode)." }
    exit 0
}
Import-Module ScheduledTasks
$user = $identity.Name
$action = New-ScheduledTaskAction -Execute $exe -Argument '--no-browser' -WorkingDirectory $PSScriptRoot
$trigger = New-ScheduledTaskTrigger -AtLogOn -User $user
$principal = New-ScheduledTaskPrincipal -UserId $user -LogonType Interactive -RunLevel Highest
$settings = New-ScheduledTaskSettingsSet -MultipleInstances IgnoreNew -ExecutionTimeLimit (New-TimeSpan -Seconds 0) -StartWhenAvailable
# Register first, then remove standard startup so a registration failure is recoverable.
Register-ScheduledTask -TaskName $taskName -Action $action -Trigger $trigger -Principal $principal -Settings $settings -Description 'Runs the localhost-only SensorDash sensor reader with access needed for NVMe measurements.' -Force | Out-Null
$shortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'SensorDash.lnk'
if (Test-Path -LiteralPath $shortcut) {
    Remove-Item -LiteralPath $shortcut -Force
    Write-Host 'Removed unelevated startup shortcut to prevent duplicate instances.'
}
Write-Host ''
Write-Host "Installed: $taskName"
Write-Host "User:      $user"
Write-Host "Program:   $exe"
Write-Host 'SensorDash will start at your next Windows sign-in; no repeat UAC prompt is needed.'
Write-Host 'Its HTTP server still binds only to http://127.0.0.1:8765.'
Write-Warning 'The local dashboard/API process will run elevated. Other software running on this PC can access its local HTTP API.'
Write-Host 'Run Remove-ElevatedStartup.cmd to undo; Install-Startup.cmd then restores standard startup.'
