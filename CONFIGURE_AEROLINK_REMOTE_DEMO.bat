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
:: Everything after the action is forwarded as given. Positional forwarding stopped at the fourth extra
:: argument and dropped anything past it without saying so; %1 rather than %~1 keeps each one quoted
:: exactly as it arrived.
set "EXTRA="
if not "%~1"=="" shift
:collectExtraArguments
if "%~1"=="" goto runConfigure
set "EXTRA=%EXTRA% %1"
shift
goto collectExtraArguments

:runConfigure
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\AeroLinkRemoteDemo.ps1" -Action Configure -ConfigureAction %CA% %EXTRA%
exit /b %ERRORLEVEL%
