@echo off
cd /d "%~dp0"

echo Building solution...
dotnet build VideoCall.sln
if errorlevel 1 (
    echo Build failed.
    pause
    exit /b 1
)

start "VideoCall Benchmark" dotnet run --no-build --project VideoCall.Benchmark.Wpf

echo Benchmark tool started.
echo Results are written to BenchmarkResults\^<test-name^>^<timestamp^>.
