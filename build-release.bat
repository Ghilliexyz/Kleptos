@echo off
setlocal

REM ============================================================
REM  Kleptos - one-step release builder
REM  Publishes win-x64 / osx-arm64 / osx-x64 / linux-x64,
REM  creates the Velopack Windows installer + update channel,
REM  and tar.gz archives for macOS/Linux.
REM  Everything ready to upload lands in .\Releases\
REM ============================================================

set "version=%~1"
if "%version%"=="" (
    set /p version="Please enter the version number (e.g., 1.2.0): "
)
if "%version%"=="" (
    echo Error: No version number provided!
    pause
    exit /b 1
)

set "VPK=%USERPROFILE%\.dotnet\tools\vpk.exe"
if not exist "%VPK%" (
    echo Error: Velopack CLI not found at %VPK%
    echo Install it with: dotnet tool install -g vpk
    pause
    exit /b 1
)

set "GITBASH=%ProgramFiles%\Git\bin\bash.exe"

echo Cleaning previous publish output...
for %%R in (win-x64 osx-arm64 osx-x64 linux-x64) do (
    if exist ".\publish\%%R" rmdir /s /q ".\publish\%%R"
)
echo.

echo === Publishing all platforms (%version%) ===
for %%R in (win-x64 osx-arm64 osx-x64 linux-x64) do (
    echo --- %%R ---
    dotnet publish Kleptos.csproj -c Release --self-contained -r %%R -o ".\publish\%%R" /p:Version=%version% --nologo
    if %ERRORLEVEL% NEQ 0 (
        echo Error: Publish failed for %%R!
        pause
        exit /b %ERRORLEVEL%
    )
)
echo.

echo === Packaging Windows installer with Velopack ===
"%VPK%" pack -u Kleptos -v %version% -p .\publish\win-x64 -e Kleptos.exe -o .\Releases
if %ERRORLEVEL% NEQ 0 (
    echo Error: Velopack packaging failed!
    pause
    exit /b %ERRORLEVEL%
)
echo.

echo === Creating macOS/Linux archives ===
if exist "%GITBASH%" (
    REM Git Bash tar preserves the executable bit; Windows tar.exe does not.
    "%GITBASH%" -c "./build-archives.sh"
    if %ERRORLEVEL% NEQ 0 (
        echo Error: Archive creation failed!
        pause
        exit /b %ERRORLEVEL%
    )
) else (
    echo WARNING: Git Bash not found at %GITBASH%.
    echo Falling back to Windows tar.exe - archives will LOSE the executable bit,
    echo so macOS/Linux users will need to run: chmod +x Kleptos
    tar -czf ".\Releases\Kleptos-macos-arm64.tar.gz" -C ".\publish\osx-arm64" .
    tar -czf ".\Releases\Kleptos-macos-x64.tar.gz" -C ".\publish\osx-x64" .
    tar -czf ".\Releases\Kleptos-linux-x64.tar.gz" -C ".\publish\linux-x64" .
)
echo.

echo ============================================================
echo  Release %version% complete. Upload everything in .\Releases\
echo  to the GitHub release:
echo    Kleptos-win-Setup.exe        - Windows installer
echo    Kleptos-win-Portable.zip     - Windows portable
echo    RELEASES / *.nupkg / assets.win.json - update channel
echo    Kleptos-macos-arm64.tar.gz   - macOS Apple Silicon
echo    Kleptos-macos-x64.tar.gz     - macOS Intel
echo    Kleptos-linux-x64.tar.gz     - Linux
echo ============================================================
pause
