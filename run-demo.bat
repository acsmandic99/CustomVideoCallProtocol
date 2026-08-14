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

start "Client 1" cmd /k dotnet run --no-build --project VideoCall.Client.Console -- 1
timeout /t 2 /nobreak >nul

start "Client 2" cmd /k dotnet run --no-build --project VideoCall.Client.Console -- 2

echo Started: Server + Client 1 + Client 2 in separate windows.
echo In Client 1 type: call 2
