@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Stop-AeroLink.ps1"
if errorlevel 1 pause
