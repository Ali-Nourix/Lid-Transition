@echo off
REM ============================================================================
REM  LidFlow - visual tuning loop
REM
REM  Builds the app, drops a config.json next to the executable (which takes
REM  precedence over the per-user one), and runs it with the developer overlay so
REM  you can edit values and re-run without touching your real settings.
REM
REM  Usage, from the repository root:
REM      scripts\TUNE.cmd            build, then run and wait for a hotkey
REM      scripts\TUNE.cmd close      build, then play one closing preview
REM      scripts\TUNE.cmd open       build, then play one opening preview
REM
REM  Edit dist\LidFlow\config.json between runs. The values that move the needle
REM  most are hingeBias, perspectiveStrength, maxBlur and shadowStrength - see
REM  docs\ARCHITECTURE.md for the full reference.
REM ============================================================================
setlocal EnableExtensions

set "ROOT=%~dp0.."
pushd "%ROOT%" || exit /b 1

set "PAYLOAD=dist\LidFlow"
set "EXE=%PAYLOAD%\LidFlow.exe"

echo Building...
call BUILD-WINDOWS.cmd -SkipTests -NoZip
if %ERRORLEVEL% neq 0 (
    echo.
    echo Build failed; not launching.
    popd
    exit /b 1
)

REM Seed a tuning config on the first run only, so edits survive a rebuild.
if not exist "%PAYLOAD%\config.json" (
    echo Seeding %PAYLOAD%\config.json from config.example.json
    copy /y config.example.json "%PAYLOAD%\config.json" >nul
)

REM Force the developer overlay on for this run without editing the config.
set "ARGS=--debug"

if /i "%~1"=="close" set "ARGS=%ARGS% --preview-close"
if /i "%~1"=="open"  set "ARGS=%ARGS% --preview-open"

echo.
echo Launching: %EXE% %ARGS%
echo   Config:  %PAYLOAD%\config.json
echo   Preview: Ctrl+Alt+Shift+C (close)  /  Ctrl+Alt+Shift+O (open)
echo   Quit:    tray icon - Exit
echo.

start "" "%EXE%" %ARGS%

popd
exit /b 0
