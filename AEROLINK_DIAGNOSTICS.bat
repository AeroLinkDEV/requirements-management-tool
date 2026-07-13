@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Get-AeroLinkDiagnostics.ps1"
pause
