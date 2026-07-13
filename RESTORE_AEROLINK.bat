@echo off
setlocal
if "%~1"=="" (
  echo Usage: RESTORE_AEROLINK.bat ^<backup-zip^> [isolated-database-name]
  echo This wrapper restores only to an isolated validation database. See product\docs\OPERATIONS.md for the separately confirmed production procedure.
  exit /b 2
)
set "TARGET=%~2"
if "%TARGET%"=="" set "TARGET=aerolink_restore_validation"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Restore-AeroLink.ps1" -BackupArchive "%~1" -TargetDatabase "%TARGET%"
exit /b %ERRORLEVEL%
