@echo off
rem Builds the standalone Nexus hub: one self-contained Nexus.exe in artifacts\Nexus\
rem (runs on any 64-bit Windows 10/11 PC, no .NET or Visual Studio needed).
rem   build\Publish-Hub.cmd            build only
rem   build\Publish-Hub.cmd install    build, then install on this PC and open it
rem Easiest: double-click Install-Hub.cmd in the repository folder.
setlocal
set "ROOT=%~dp0.."

where dotnet >nul 2>nul
if errorlevel 1 (
  echo The .NET SDK was not found. Install Visual Studio 2026 ^(or the .NET 10 SDK^) and try again.
  exit /b 1
)

echo Building the Nexus hub (the first build downloads packages and takes a minute)...
rem All publish settings are on the command line (no publish profile needed).
dotnet publish "%ROOT%\src\Nexus.Hub\Nexus.Hub.csproj" -c Release -r win-x64 --self-contained true ^
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true ^
  -p:DebugType=embedded -p:AllowedReferenceRelatedFileExtensions=none ^
  -o "%ROOT%\artifacts\Nexus" -nologo -v:minimal
if errorlevel 1 (
  echo.
  echo Build failed.
  exit /b 1
)
if not exist "%ROOT%\artifacts\Nexus\Nexus.exe" (
  echo.
  echo Build finished but artifacts\Nexus\Nexus.exe is missing.
  exit /b 1
)
copy /y "%ROOT%\deploy\hub\Install.cmd" "%ROOT%\artifacts\Nexus\" >nul
echo.
echo Built: %ROOT%\artifacts\Nexus\Nexus.exe

if /i "%~1"=="install" (
  echo Installing to %LOCALAPPDATA%\Nexus\Hub ...
  "%ROOT%\artifacts\Nexus\Nexus.exe" --install
  if errorlevel 1 exit /b 1
  rem --install starts the installed hub and returns; give it a moment to come up.
  timeout /t 2 /nobreak >nul
) else (
  echo To install it: run Install.cmd in that folder, or Nexus.exe --install.
)
exit /b 0
