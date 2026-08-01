@echo off
setlocal
title Configure AeroLink Daily Backup
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Configure-AeroLinkBackupSchedule.ps1" %*
if errorlevel 1 (
  echo.
  echo AeroLink backup schedule configuration failed. Review the error above.
  pause
  exit /b 1
)
echo.
echo Use -Action Status to inspect the schedule or -Action Remove to remove it.
pause

