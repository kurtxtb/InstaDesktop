@echo off
setlocal
cd /d "%~dp0"
set "DOTNET_CLI_HOME=%~dp0.build"
set "NUGET_PACKAGES=%~dp0.nuget\packages"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
dotnet publish InstaDesktop.csproj -c Release -p:PublishProfile=SingleFile
if errorlevel 1 goto failed
echo.
echo Build completed.
echo EXE: %~dp0publish\single-file\InstaDesktop.exe
echo Editable Assets are included and recreated from embedded defaults if missing.
if /i not "%~1"=="--no-pause" pause
exit /b 0
:failed
echo Single-file publish failed. The stable publish is recommended.
if /i not "%~1"=="--no-pause" pause
exit /b 1
