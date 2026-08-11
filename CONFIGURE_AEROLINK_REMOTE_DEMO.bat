@echo off
setlocal
title AeroLink Remote Demo - Scheduled Recovery Configuration
cd /d "%~dp0"
set "CA=%~1"
if "%CA%"=="" set "CA=Preview"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\AeroLinkRemoteDemo.ps1" -Action Configure -ConfigureAction %CA% %2 %3 %4 %5
exit /b %ERRORLEVEL%
