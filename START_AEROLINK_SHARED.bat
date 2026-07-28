@echo off
setlocal
title AeroLink Shared Launcher
cd /d "%~dp0"

echo.
echo ============================================================
echo   AeroLink - Start and share on this network
echo ============================================================
echo.
echo Same production build as START_AEROLINK_PRODUCTION.bat, but
echo other people on this network can open it from their own
echo machines. The address to give them is printed at the end.
echo.
echo Anyone who can reach it can sign in with the demonstration
echo password, and nothing is encrypted. Fine for showing people
echo at work; not a deployment.
echo.

set "AEROLINK_SCRIPT=Start-AeroLinkProduction.ps1"
set "AEROLINK_ARGS=-Shared %*"
call "%~dp0product\scripts\launch.cmd"
exit /b %ERRORLEVEL%
