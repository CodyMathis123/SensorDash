$shortcut = Join-Path ([Environment]::GetFolderPath('Startup')) 'SensorDash.lnk'
if (Test-Path $shortcut) { Remove-Item $shortcut -Force }
Write-Host 'SensorDash removed from Windows startup.'
