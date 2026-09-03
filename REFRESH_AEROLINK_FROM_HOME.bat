@echo off
setlocal
:: Windows PowerShell must load its own modules. A PowerShell 7 parent leaves the 7.x module directories
:: first in PSModulePath, and 5.1 then binds Microsoft.PowerShell.Utility from there and loses cmdlets it
:: needs, which surfaces later as an unrelated error deep in a script. Clearing the variable makes
:: PowerShell rebuild its own default, so a launcher behaves the same from Explorer, cmd, or a pwsh prompt.
set "PSModulePath="
title AeroLink - Refresh this laptop from a HOME snapshot
cd /d "%~dp0"
echo.
echo ============================================================
echo   AeroLink - Refresh this laptop from a HOME snapshot
echo ============================================================
echo.
echo One-way and explicit. Normal startup never does this, and this
echo laptop keeps working when HOME is unreachable. Activation
echo REPLACES this laptop's AeroLink database with the snapshot;
echo the current state is backed up first and the snapshot is
echo validated on an isolated copy before anything is replaced.
echo.
echo   REFRESH_AEROLINK_FROM_HOME.bat "<archive.zip>"
echo   REFRESH_AEROLINK_FROM_HOME.bat "<archive.zip>" Import REFRESH-FROM-HOME
echo.
if "%~1"=="" (
  echo Give the path to a HOME AeroLink backup archive.
  exit /b 1
)
set "SNAPSHOT=%~1"
set "ACTION=%~2"
if "%ACTION%"=="" set "ACTION=Preview"
set "CONFIRM=%~3"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Import-AeroLinkHomeSnapshot.ps1" -SnapshotArchive "%SNAPSHOT%" -Action %ACTION% -Confirmation "%CONFIRM%"
exit /b %ERRORLEVEL%
