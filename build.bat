@echo off
echo Starting build and packaging process...

echo Publishing .NET project...
dotnet publish Kleptos.csproj -c Release --self-contained -r win-x64 -o .\publish
if %ERRORLEVEL% NEQ 0 (
    echo Error: Publish failed!
    pause
    exit /b %ERRORLEVEL%
)

echo Creating VPK package...
vpk pack -u Kleptos -v 1.0.0 -p .\publish -e Kleptos.exe
if %ERRORLEVEL% NEQ 0 (
    echo Error: VPK packaging failed!
    pause
    exit /b %ERRORLEVEL%
)

echo Process completed successfully!
pause