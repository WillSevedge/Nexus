@echo off
rem Builds the standalone Nexus hub: one self-contained Nexus.exe in artifacts\Nexus\
rem (runs on any 64-bit Windows 10/11 PC, no .NET or Visual Studio needed).
rem   build\Publish-Hub.cmd            build only
rem   build\Publish-Hub.cmd install    build, then install on this PC and open it
setlocal
set "ROOT=%~dp0.."
dotnet publish "%ROOT%\src\Nexus.Hub\Nexus.Hub.csproj" -p:PublishProfile=Standalone
if errorlevel 1 exit /b 1
copy /y "%ROOT%\deploy\hub\Install.cmd" "%ROOT%\artifacts\Nexus\" >nul
echo.
echo Nexus hub: %ROOT%\artifacts\Nexus\Nexus.exe
echo Install it by running Install.cmd in that folder (or: Nexus.exe --install).
if /i "%~1"=="install" "%ROOT%\artifacts\Nexus\Nexus.exe" --install
