@echo off
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-ElevatedStartup.ps1"
if errorlevel 1 echo Elevated installation failed. See the error above.
pause
