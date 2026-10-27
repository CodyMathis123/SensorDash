# Run from the extracted SensorDash directory. No elevation is required.
$ErrorActionPreference = 'Stop'
$exe = Join-Path $PSScriptRoot 'SensorDash.exe'
if (!(Test-Path $exe)) { throw "Missing SensorDash.exe. Extract the entire release first." }
$startup = [Environment]::GetFolderPath('Startup')
$shortcutPath = Join-Path $startup 'SensorDash.lnk'
$shell = New-Object -ComObject WScript.Shell
$shortcut = $shell.CreateShortcut($shortcutPath)
$shortcut.TargetPath = $exe
$shortcut.Arguments = '--no-browser'
$shortcut.WorkingDirectory = $PSScriptRoot
$shortcut.Description = 'SensorDash local hardware telemetry'
$shortcut.Save()
Write-Host "SensorDash will now start when you sign in: $shortcutPath"
