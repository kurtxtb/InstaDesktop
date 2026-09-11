@echo off
setlocal
cd /d "%~dp0"
set "DOTNET_CLI_HOME=%~dp0.build"
set "NUGET_PACKAGES=%~dp0.nuget\packages"
set "DOTNET_CLI_TELEMETRY_OPTOUT=1"
set "DOTNET_SKIP_FIRST_TIME_EXPERIENCE=1"
where dotnet >nul 2>nul
if errorlevel 1 goto no_sdk
dotnet restore InstaDesktop.csproj
if errorlevel 1 goto failed
dotnet build InstaDesktop.csproj -c Release --no-restore
if errorlevel 1 goto failed
dotnet publish InstaDesktop.csproj -c Release -p:PublishProfile=Stable
if errorlevel 1 goto failed
echo.
echo Build completed.
echo EXE: %~dp0publish\win-x64\InstaDesktop.exe
echo Keep the entire publish\win-x64 folder together.
if /i not "%~1"=="--no-pause" pause
exit /b 0
:no_sdk
echo .NET SDK 8.0.300 or newer is required.
goto failed
:failed
echo Build failed. Read the errors above.
if /i not "%~1"=="--no-pause" pause
exit /b 1
