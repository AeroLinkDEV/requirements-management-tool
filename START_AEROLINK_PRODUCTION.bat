@echo off
setlocal
title AeroLink Production Build Launcher
cd /d "%~dp0"

echo.
echo ============================================================
echo   AeroLink - Start from a production build
echo ============================================================
echo.
echo Builds the website, then serves it from the API on one port.
echo Use this for demonstrations. START_AEROLINK.bat runs the
echo development server instead, which is for development.
echo.

set "AEROLINK_SCRIPT=Start-AeroLinkProduction.ps1"
set "AEROLINK_ARGS=%*"
call "%~dp0product\scripts\launch.cmd"
exit /b %ERRORLEVEL%
