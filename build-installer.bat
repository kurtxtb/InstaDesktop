@echo off
setlocal
cd /d "%~dp0"
if not exist "publish\win-x64\InstaDesktop.exe" (
    echo Run build-release.bat first.
    goto failed
)
if defined ISCC_PATH goto compile
set "ISCC_PATH=%ProgramFiles(x86)%\Inno Setup 6\ISCC.exe"
if exist "%ISCC_PATH%" goto compile
set "ISCC_PATH=%LOCALAPPDATA%\Programs\Inno Setup 6\ISCC.exe"
if exist "%ISCC_PATH%" goto compile
echo Inno Setup 6 is required. Set ISCC_PATH to ISCC.exe if installed elsewhere.
goto failed
:compile
"%ISCC_PATH%" /Qp installer\InstaDesktop.iss
if errorlevel 1 goto failed
echo Installer: %~dp0publish\installer\InstaDesktop-Setup.exe
if /i not "%~1"=="--no-pause" pause
exit /b 0
:failed
if /i not "%~1"=="--no-pause" pause
exit /b 1
