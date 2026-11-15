@echo off
rem ---------------------------------------------------------------------------
rem  Builds the Nexus add-ins for every supported program year (2024-2027):
rem  Revit, and AutoCAD / Civil 3D / Plant 3D.
rem   - Installs each year's add-in on this PC when that year is installed here.
rem   - Collects every year under artifacts\addins\ with Install-Addins.cmd, to copy
rem     to other PCs (double-click Install-Addins.cmd there; no Visual Studio needed).
rem  Close Revit and AutoCAD first, or their add-ins are not updated.
rem  The hub is the same for every year: build it with Install-Hub.cmd.
rem ---------------------------------------------------------------------------
setlocal EnableDelayedExpansion
set "ROOT=%~dp0.."
set "STAGE=%ROOT%\artifacts\addins"
set YEARS=2024 2025 2026 2027

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET SDK was not found. Install Visual Studio 2026 ^(or the .NET 10 SDK^) and try again.
  exit /b 1
)

if exist "%STAGE%" rmdir /s /q "%STAGE%"
set FAILED=

for %%Y in (%YEARS%) do (
  echo.
  echo ==== %%Y ====
  for %%P in (Nexus.Agent.Revit Nexus.Agent.Acad Nexus.Agent.Acad.Civil3D Nexus.Agent.Acad.Plant3D Nexus.Agent.Acad.Console) do (
    dotnet build "%ROOT%\src\%%P\%%P.csproj" -c Release -p:HostYear=%%Y "-p:NexusStageDir=%STAGE%" -nologo -v:minimal
    if errorlevel 1 set FAILED=!FAILED! %%P-%%Y
  )
)

copy /y "%ROOT%\deploy\Install-Addins.cmd" "%STAGE%\" >nul

rem What is on this PC now, so there is no guessing which add-ins were updated.
echo.
echo ==== Nexus add-ins on this PC ====
set ANY=
for %%Y in (%YEARS%) do (
  if exist "%APPDATA%\Autodesk\Revit\Addins\%%Y\Nexus\Nexus.Agent.Revit.dll" (
    for %%F in ("%APPDATA%\Autodesk\Revit\Addins\%%Y\Nexus\Nexus.Agent.Revit.dll") do echo   Revit %%Y: installed, built %%~tF
    set ANY=1
  )
)
for %%Y in (%YEARS%) do (
  if exist "%APPDATA%\Autodesk\ApplicationPlugins\Nexus.bundle\Contents\%%Y\Nexus.Agent.Acad.dll" (
    for %%F in ("%APPDATA%\Autodesk\ApplicationPlugins\Nexus.bundle\Contents\%%Y\Nexus.Agent.Acad.dll") do echo   AutoCAD / Civil 3D / Plant 3D %%Y: installed, built %%~tF
    set ANY=1
  )
)
if not defined ANY echo   None. No Revit or AutoCAD 2024-2027 was found on this PC.
echo   A time that is not from just now means that add-in was NOT updated ^(its program was open, or its build failed^).

echo.
if defined FAILED (
  echo Some builds failed: %FAILED%
  exit /b 1
)
echo All years built. To set up another PC, copy %STAGE% there and double-click Install-Addins.cmd.
exit /b 0
