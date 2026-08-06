@echo off
setlocal
cd /d "%~dp0"
powershell.exe -NoProfile -ExecutionPolicy Bypass -File ".\product\scripts\Install-AeroLinkDocumentConnector.ps1"
if errorlevel 1 (
  echo.
  echo The AeroLink desktop connector was not installed. Review the error above.
  pause
  exit /b 1
)
echo.
echo Installation complete. Return to AeroLink and choose Open in Word.
pause
