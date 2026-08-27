@echo off
setlocal
cd /d "%~dp0"

set DOTNET_EXE=
if exist "D:\Program Files\dotnet\dotnet.exe" set "DOTNET_EXE=D:\Program Files\dotnet\dotnet.exe"
if not defined DOTNET_EXE if exist "D:\dotnet\dotnet.exe" set "DOTNET_EXE=D:\dotnet\dotnet.exe"
if not defined DOTNET_EXE set "DOTNET_EXE=dotnet"

"%DOTNET_EXE%" run --project src\F1TelemetryLab.App\F1TelemetryLab.App.csproj -c Debug
if errorlevel 1 (
  echo.
  echo App failed. Copy this text and send it to ChatGPT.
  pause
)
