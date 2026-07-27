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

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Start-AeroLinkProduction.ps1" -Shared %*
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
