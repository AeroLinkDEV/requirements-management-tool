@echo off
setlocal
cd /d "%~dp0"

rem Windows-friendly changed-area validation. Examples:
rem   TEST_AEROLINK_CHANGED.bat -SinceOriginMain -Explain -DryRun
rem   TEST_AEROLINK_CHANGED.bat -Paths product\client\src\App.tsx -Mode Fast
rem   TEST_AEROLINK_CHANGED.bat -Mode Full
rem
rem The PowerShell launcher asks the shared planner for one dry-run decision before real execution.
rem It reuses a backend build only when that same Fast plan selected it, and fails PostgreSQL Full
rem immediately when Docker cannot run the required Linux postgres:17 disposable container.

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0product\scripts\Invoke-AeroLinkChangedValidation.ps1" %*
set "RESULT=%ERRORLEVEL%"

echo.
if not "%RESULT%"=="0" (
  echo AeroLink changed validation did not complete. Review the error above.
  exit /b %RESULT%
)
echo AeroLink changed validation finished. GitHub Actions full evidence is still required for merge.
exit /b 0
