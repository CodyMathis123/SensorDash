$ErrorActionPreference = 'Stop'
$taskName = 'SensorDash (Elevated)'
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
$isAdmin = (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
if (-not $isAdmin) {
    Write-Host 'Requesting administrator permission to remove the SensorDash logon task...'
    $argsToPass = '-NoProfile -ExecutionPolicy Bypass -File "{0}"' -f $PSCommandPath.Replace('"','')
    $child = Start-Process -FilePath 'powershell.exe' -Verb RunAs -ArgumentList $argsToPass -Wait -PassThru
    if ($child.ExitCode -ne 0) { throw "Removal failed with exit code $($child.ExitCode)." }
    exit 0
}
Import-Module ScheduledTasks
$existing = Get-ScheduledTask -TaskName $taskName -ErrorAction SilentlyContinue
if ($null -ne $existing) {
    Unregister-ScheduledTask -TaskName $taskName -Confirm:$false
    Write-Host "Removed $taskName."
} else {
    Write-Host 'No SensorDash elevated logon task was installed.'
}
Write-Host 'This does not stop a currently running SensorDash instance.'
Write-Host 'To restore unelevated startup, run Install-Startup.cmd.'
