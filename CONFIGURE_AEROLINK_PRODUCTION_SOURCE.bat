@echo off
setlocal
:: Windows PowerShell must load its own modules. A PowerShell 7 parent leaves the 7.x module directories
:: first in PSModulePath, and 5.1 then binds Microsoft.PowerShell.Utility from there and loses cmdlets it
:: needs, which surfaces later as an unrelated error deep in a script. Clearing the variable makes
:: PowerShell rebuild its own default, so a launcher behaves the same from Explorer, cmd, or a pwsh prompt.
set "PSModulePath="
title AeroLink Production Source - Configuration
cd /d "%~dp0"
echo.
echo ============================================================
echo   AeroLink - Dedicated HOME production source
echo ============================================================
echo.
echo HOME production and the protected remote demo run from their own
echo clean source checkout, so development on any branch - dirty work
echo included - cannot take the demo offline. Persistent data is NOT
echo separated: the production source uses this machine's canonical
echo AeroLink database, evidence and backups.
echo.
echo   Preview  show what Install would do and change nothing
echo   Install  create the dedicated production source
echo   Status   where it is and whether it is canonical
echo   Update   fast-forward it to current origin/main
echo.
set "CA=%~1"
if "%CA%"=="" set "CA=Preview"
:: Everything after the action is forwarded as given, each argument still quoted exactly as it arrived.
set "EXTRA="
if not "%~1"=="" shift
:collectExtraArguments
if "%~1"=="" goto runConfigure
set "EXTRA=%EXTRA% %1"
shift
goto collectExtraArguments

:runConfigure
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Configure-AeroLinkProductionSource.ps1" -Action %CA% %EXTRA%
exit /b %ERRORLEVEL%
