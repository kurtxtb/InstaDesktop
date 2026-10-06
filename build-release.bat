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
rem Start from an empty folder so files removed from the app never reach the installer.
rem Moving the folder fails as a whole while InstaDesktop runs from it, so a
rem running copy is never left with half of its files deleted.
if exist "publish\win-x64.old" rmdir /s /q "publish\win-x64.old"
if exist "publish\win-x64" move "publish\win-x64" "publish\win-x64.old" >nul 2>nul
if exist "publish\win-x64" goto locked
if exist "publish\win-x64.old" rmdir /s /q "publish\win-x64.old"
dotnet publish InstaDesktop.csproj -c Release -p:PublishProfile=Stable
if errorlevel 1 goto failed
echo.
echo Build completed.
echo EXE: %~dp0publish\win-x64\InstaDesktop.exe
echo Keep the entire publish\win-x64 folder together.
if /i not "%~1"=="--no-pause" pause
exit /b 0
:locked
echo publish\win-x64 is in use. Close InstaDesktop started from that folder and try again.
goto failed
:no_sdk
echo .NET SDK 8.0.300 or newer is required.
goto failed
:failed
echo Build failed. Read the errors above.
if /i not "%~1"=="--no-pause" pause
exit /b 1
