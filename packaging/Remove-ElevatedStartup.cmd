@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Remove-ElevatedStartup.ps1"
if errorlevel 1 echo Elevated task removal failed. See the error above.
pause
