@echo off
setlocal
:: Windows PowerShell must load its own modules. A PowerShell 7 parent leaves the 7.x module directories
:: first in PSModulePath, and 5.1 then binds Microsoft.PowerShell.Utility from there and loses cmdlets it
:: needs, which surfaces later as an unrelated error deep in a script. Clearing the variable makes
:: PowerShell rebuild its own default, so a launcher behaves the same from Explorer, cmd, or a pwsh prompt.
set "PSModulePath="
title AeroLink Remote Demo - Scheduled Recovery Configuration
cd /d "%~dp0"
set "CA=%~1"
if "%CA%"=="" set "CA=Preview"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\AeroLinkRemoteDemo.ps1" -Action Configure -ConfigureAction %CA% %2 %3 %4 %5
exit /b %ERRORLEVEL%
