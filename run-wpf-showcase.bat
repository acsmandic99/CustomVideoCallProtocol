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

start "VideoCall Showcase 1" dotnet run --no-build --project VideoCall.Client.Wpf.Showcase
timeout /t 2 /nobreak >nul

start "VideoCall Showcase 2" dotnet run --no-build --project VideoCall.Client.Wpf.Showcase

echo Started: Server + 2 showcase clients.
echo In each client: enter a name, press Sign in, then one calls the other.
