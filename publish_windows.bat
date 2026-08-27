@echo off
setlocal
cd /d "%~dp0"

set DOTNET_EXE=
if exist "D:\Program Files\dotnet\dotnet.exe" set "DOTNET_EXE=D:\Program Files\dotnet\dotnet.exe"
if not defined DOTNET_EXE if exist "D:\dotnet\dotnet.exe" set "DOTNET_EXE=D:\dotnet\dotnet.exe"
if not defined DOTNET_EXE set "DOTNET_EXE=dotnet"

set OUT_DIR=D:\F1TelemetryLab\builds\F1TelemetryLab
mkdir "D:\F1TelemetryLab\builds" 2>nul

echo Publishing portable Windows folder to:
echo %OUT_DIR%
"%DOTNET_EXE%" publish src\F1TelemetryLab.App\F1TelemetryLab.App.csproj -c Release -r win-x64 --self-contained false -o "%OUT_DIR%"
if errorlevel 1 goto error

echo.
echo Publish OK.
echo EXE: %OUT_DIR%\F1TelemetryLab.exe
pause
exit /b 0

:error
echo.
echo Publish failed. Copy this window text and send it to ChatGPT.
pause
exit /b 1
