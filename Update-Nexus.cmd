@echo off
rem ---------------------------------------------------------------------------
rem  Double-click this file to update everything Nexus on this PC from this folder:
rem    1. gets the latest code (git pull),
rem    2. builds and installs the add-ins for Revit and AutoCAD / Civil 3D / Plant 3D,
rem    3. builds and installs the Nexus hub, and starts it.
rem  Close Revit and AutoCAD first: Windows locks an add-in while its program runs.
rem ---------------------------------------------------------------------------
title Update Nexus
cd /d "%~dp0"

set OPEN=
tasklist /fi "imagename eq Revit.exe" | find /i "Revit.exe" >nul && set OPEN=Revit
tasklist /fi "imagename eq acad.exe" | find /i "acad.exe" >nul && set OPEN=%OPEN% AutoCAD
if defined OPEN (
  echo %OPEN% is open. Save your work and close it, then run this again.
  echo ^(Windows locks the add-in files while the program runs, so they cannot be updated.^)
  echo.
  pause
  exit /b 1
)

where git >nul 2>nul
if not errorlevel 1 (
  echo ==== 1 of 3: getting the latest Nexus code ====
  git pull --ff-only
  if errorlevel 1 echo git pull did not finish ^(local changes?^). Building the code that is here.
  echo.
)

echo ==== 2 of 3: add-ins for Revit and AutoCAD ====
set ADDINS=ok
call "%~dp0build\Build-AllYears.cmd"
if errorlevel 1 set ADDINS=failed
echo.

echo ==== 3 of 3: the Nexus hub ====
set HUB=ok
call "%~dp0build\Publish-Hub.cmd" install
if errorlevel 1 set HUB=failed

echo.
echo ==== Result ====
if "%ADDINS%"=="ok" (echo   Add-ins: updated. See the list above for the years on this PC.) else (echo   Add-ins: SOME FAILED. See "Some builds failed" above.)
if "%HUB%"=="ok" (echo   Hub: updated and running ^(the N icon next to the clock^).) else (echo   Hub: NOT updated. See the messages above.)
echo.
echo Start Revit or AutoCAD, then open Nexus from the tray.
echo.
pause
