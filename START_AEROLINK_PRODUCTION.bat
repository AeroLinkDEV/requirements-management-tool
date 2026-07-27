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

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Start-AeroLinkProduction.ps1" %*
set "RESULT=%ERRORLEVEL%"

echo.
if not "%RESULT%"=="0" (
  echo AeroLink could not be started. Review the error above.
  echo Logs are stored in product\.local\logs\
  echo.
  pause
  exit /b %RESULT%
)

echo AeroLink is ready. Your browser should now be open.
echo You may close this window; the local service will keep running.
echo.
pause
exit /b 0
