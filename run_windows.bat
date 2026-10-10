@echo off
setlocal
cd /d "%~dp0"

if exist "%~dp0F1TelemetryLab.exe" (
  start "" "%~dp0F1TelemetryLab.exe"
  exit /b
)
if exist "%~dp0artifacts\F1TelemetryLab-win-x64\F1TelemetryLab.exe" (
  start "" "%~dp0artifacts\F1TelemetryLab-win-x64\F1TelemetryLab.exe"
  exit /b
)
echo Starting from source. The first run may download .NET dependencies and build the app.
set DOTNET_EXE=
if exist "D:\Program Files\dotnet\dotnet.exe" set "DOTNET_EXE=D:\Program Files\dotnet\dotnet.exe"
if not defined DOTNET_EXE if exist "D:\dotnet\dotnet.exe" set "DOTNET_EXE=D:\dotnet\dotnet.exe"
if not defined DOTNET_EXE set "DOTNET_EXE=dotnet"

"%DOTNET_EXE%" run --project src\F1TelemetryLab.App\F1TelemetryLab.App.csproj -c Release
if errorlevel 1 (
  echo.
  echo App failed. Copy this text and send it to ChatGPT.
  pause
)
