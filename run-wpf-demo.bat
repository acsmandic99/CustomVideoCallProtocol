@echo off
cd /d "%~dp0"

echo Building solution...
dotnet build VideoCall.sln
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

start "VideoCall Server" cmd /k dotnet run --no-build --project VideoCall.Server.Console
timeout /t 3 /nobreak >nul

start "VideoCall Client 1" dotnet run --no-build --project VideoCall.Client.Wpf
timeout /t 2 /nobreak >nul

start "VideoCall Client 2" dotnet run --no-build --project VideoCall.Client.Wpf

echo Started: Server + 2 WPF clients.
echo In each client: enter a name, press Register, then one calls the other.
