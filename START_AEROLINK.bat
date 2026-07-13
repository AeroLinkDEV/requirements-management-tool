@echo off
setlocal
title AeroLink Local Launcher
cd /d "%~dp0"

echo.
echo ============================================================
echo   AeroLink - Start local website
echo ============================================================
echo.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Start-AeroLink.ps1"
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
echo You may close this window; the local services will keep running.
echo.
pause
exit /b 0
