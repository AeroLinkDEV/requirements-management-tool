@echo off
setlocal
:: Windows PowerShell must load its own modules. A PowerShell 7 parent leaves the 7.x module directories
:: first in PSModulePath, and 5.1 then binds Microsoft.PowerShell.Utility from there and loses cmdlets it
:: needs, which surfaces later as an unrelated error deep in a script. Clearing the variable makes
:: PowerShell rebuild its own default, so a launcher behaves the same from Explorer, cmd, or a pwsh prompt.
set "PSModulePath="
if "%~1"=="" (
  echo Usage: RESTORE_AEROLINK.bat ^<backup-zip^> [isolated-database-name]
  echo This wrapper restores only to an isolated validation database. See product\docs\OPERATIONS.md for the separately confirmed production procedure.
  exit /b 2
)
set "TARGET=%~2"
if "%TARGET%"=="" set "TARGET=aerolink_restore_validation"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Restore-AeroLink.ps1" -BackupArchive "%~1" -TargetDatabase "%TARGET%"
exit /b %ERRORLEVEL%
