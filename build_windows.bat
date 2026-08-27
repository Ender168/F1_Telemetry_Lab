@echo off
setlocal
cd /d "%~dp0"

set DOTNET_EXE=
if exist "D:\Program Files\dotnet\dotnet.exe" set "DOTNET_EXE=D:\Program Files\dotnet\dotnet.exe"
if not defined DOTNET_EXE if exist "D:\dotnet\dotnet.exe" set "DOTNET_EXE=D:\dotnet\dotnet.exe"
if not defined DOTNET_EXE set "DOTNET_EXE=dotnet"

echo Using: %DOTNET_EXE%
"%DOTNET_EXE%" --info
if errorlevel 1 goto error

echo.
echo Restoring packages...
"%DOTNET_EXE%" restore F1TelemetryLab.sln
if errorlevel 1 goto error

echo.
echo Building Debug...
"%DOTNET_EXE%" build F1TelemetryLab.sln -c Debug --no-restore
if errorlevel 1 goto error

echo.
echo Build OK.
echo Run with: run_windows.bat
pause
exit /b 0

:error
echo.
echo Build failed. Copy this window text and send it to ChatGPT. Yes, the machine has opinions.
pause
exit /b 1
