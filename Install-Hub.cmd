@echo off
rem ---------------------------------------------------------------------------
rem  Double-click this file to build the Nexus hub and install (or update) it.
rem  Same as running:  build\Publish-Hub.cmd install
rem  Close nothing first: a running Nexus is stopped and restarted automatically.
rem ---------------------------------------------------------------------------
title Install Nexus hub
cd /d "%~dp0"
call "%~dp0build\Publish-Hub.cmd" install
if errorlevel 1 (
  echo.
  echo The hub was NOT installed. See the messages above.
) else (
  echo.
  echo Done. Nexus is open. Its N icon is in the system tray next to the clock,
  echo or under the small up arrow that shows hidden icons.
)
echo.
pause
