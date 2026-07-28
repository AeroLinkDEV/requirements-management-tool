@echo off
setlocal
title AeroLink Local Launcher
cd /d "%~dp0"

echo.
echo ============================================================
echo   AeroLink - Start local website
echo ============================================================
echo.

set "AEROLINK_SCRIPT=Start-AeroLink.ps1"
set "AEROLINK_ARGS=%*"
call "%~dp0product\scripts\launch.cmd"
exit /b %ERRORLEVEL%
