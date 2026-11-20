# Read-only SensorDash collector diagnostics. Run in an elevated PowerShell session
# to include System/Application event logs; no credentials or application settings
# are included in the archive.
[CmdletBinding()]
param(
    [int]$Days = 7,
    [string]$OutputDirectory = (Join-Path $env:TEMP 'SensorDash-Diagnostics')
)
$ErrorActionPreference = 'Stop'
if ($Days -lt 1 -or $Days -gt 90) { throw 'Days must be between 1 and 90.' }
$stamp = Get-Date -Format 'yyyyMMdd-HHmmss'
$work = Join-Path $OutputDirectory "collector-$stamp"
New-Item -ItemType Directory -Path $work -Force | Out-Null
$service = 'SensorDashCollector'
$since = (Get-Date).AddDays(-$Days)

function Save-Text([string]$name, [scriptblock]$body) {
    try {
        (& $body | Out-String -Width 300) |
            Set-Content -LiteralPath (Join-Path $work $name) -Encoding utf8
    }
    catch {
        "Collection failed: $($_.Exception.Message)" |
            Set-Content -LiteralPath (Join-Path $work $name) -Encoding utf8
    }
}

Save-Text 'service.txt' {
    Get-CimInstance Win32_Service -Filter "Name='$service'" |
        Select-Object Name, State, StartMode, StartName, ProcessId, ExitCode, PathName |
        Format-List
    & sc.exe qc $service
    & sc.exe qfailure $service
}
Save-Text 'system-events.txt' {
    Get-WinEvent -FilterHashtable @{LogName='System'; StartTime=$since} -ErrorAction SilentlyContinue |
        Where-Object { $_.ProviderName -eq 'Service Control Manager' -and $_.Message -match 'SensorDash' } |
        Select-Object TimeCreated, Id, ProviderName, Message | Format-List
}
Save-Text 'application-events.txt' {
    Get-WinEvent -FilterHashtable @{LogName='Application'; StartTime=$since} -ErrorAction SilentlyContinue |
        Where-Object {
            $_.ProviderName -in @('.NET Runtime', 'Application Error', 'Windows Error Reporting', 'SensorDashCollector') -and
            $_.Message -match 'SensorDash'
        } | Select-Object TimeCreated, Id, ProviderName, Message | Format-List
}
$logDir = Join-Path $env:ProgramData 'SensorDash\Logs'
if (Test-Path $logDir) {
    Get-ChildItem -LiteralPath $logDir -File -Filter 'collector-startup.log*' |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination $work }
}
Save-Text 'environment.txt' {
    [pscustomobject]@{
        ComputerName = $env:COMPUTERNAME
        WindowsVersion = [Environment]::OSVersion.VersionString
        CollectedUtc = (Get-Date).ToUniversalTime().ToString('O')
        StorageSafeModeEnabled = [Environment]::GetEnvironmentVariable('SENSORDASH_DISABLE_STORAGE','Machine') -eq '1'
    } | Format-List
}
New-Item -ItemType Directory -Force -Path $OutputDirectory | Out-Null
$zip = Join-Path $OutputDirectory "SensorDash-Diagnostics-$stamp.zip"
Compress-Archive -Path (Join-Path $work '*') -DestinationPath $zip -Force
Write-Host "Diagnostics: $zip"
Write-Warning 'Review diagnostic files before sharing; Windows event messages may contain local paths or device details.'
