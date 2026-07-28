@echo off
:: The part every double-clickable launcher in the repository root shares: run the PowerShell script, report a
:: failure the same way, and keep the window open so somebody who double-clicked can read it.
::
:: Each root .bat keeps its own title and banner. That is the one thing that genuinely differs between them, and
:: it is the only part the person double-clicking actually reads.
::
:: Inputs arrive as environment variables rather than as arguments on purpose. Forwarding a variable number of
:: arguments through a called batch file means a shift loop, and a shift loop is how quoting bugs get into the
:: one script that has to work on a machine nobody can debug.
::
::   AEROLINK_SCRIPT  the PowerShell file in this directory to run
::   AEROLINK_ARGS    arguments to pass it, already including anything the caller was given

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0%AEROLINK_SCRIPT%" %AEROLINK_ARGS%
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
