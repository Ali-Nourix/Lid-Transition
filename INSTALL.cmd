@echo off
REM ============================================================================
REM  LidFlow - install
REM
REM  Copies LidFlow to %LOCALAPPDATA%\Programs\LidFlow, registers it to start at
REM  sign-in, and launches it.
REM
REM  No administrator rights are needed and nothing is written outside the
REM  current user's profile. The only registry change is the per-user Run value,
REM  which Task Manager's Startup tab can disable and UNINSTALL.cmd removes.
REM ============================================================================
setlocal EnableExtensions

set "SOURCE_DIR=%~dp0"
set "TARGET_DIR=%LOCALAPPDATA%\Programs\LidFlow"
set "EXE=%TARGET_DIR%\LidFlow.exe"

echo.
echo Installing LidFlow
echo   from %SOURCE_DIR%
echo   to   %TARGET_DIR%
echo.

if not exist "%SOURCE_DIR%LidFlow.exe" (
    echo INSTALL FAILED
    echo   LidFlow.exe was not found next to this script.
    echo   Run INSTALL.cmd from the folder produced by BUILD-WINDOWS.cmd
    echo   ^(dist\LidFlow^) or from the extracted release archive.
    echo.
    pause
    exit /b 1
)

REM Stop a running copy first, otherwise the copy below fails on a locked file.
tasklist /fi "imagename eq LidFlow.exe" 2>nul | find /i "LidFlow.exe" >nul
if %ERRORLEVEL% equ 0 (
    echo Stopping the running instance...
    taskkill /im LidFlow.exe /f >nul 2>&1
    REM Give the tray icon time to disappear before the files move.
    ping -n 3 127.0.0.1 >nul
)

if not exist "%TARGET_DIR%" mkdir "%TARGET_DIR%" 2>nul

robocopy "%SOURCE_DIR%." "%TARGET_DIR%" /E /NFL /NDL /NJH /NJS /NP /R:2 /W:1 /XF INSTALL.cmd UNINSTALL.cmd >nul
if %ERRORLEVEL% GEQ 8 (
    echo INSTALL FAILED
    echo   Copying files to "%TARGET_DIR%" did not succeed ^(robocopy exit %ERRORLEVEL%^).
    echo.
    pause
    exit /b 1
)

REM Ship the uninstaller alongside the app so it can be found later.
copy /y "%SOURCE_DIR%UNINSTALL.cmd" "%TARGET_DIR%\UNINSTALL.cmd" >nul 2>&1

echo Registering LidFlow to start at sign-in...
reg add "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v LidFlow /t REG_SZ /d "\"%EXE%\"" /f >nul
if %ERRORLEVEL% neq 0 (
    echo   WARNING: the start-up entry could not be written.
    echo            LidFlow is installed and can be started manually.
)

echo.
echo Installed.
echo   Executable     %EXE%
echo   Configuration  %LOCALAPPDATA%\LidFlow\config.json
echo   Logs           %LOCALAPPDATA%\LidFlow\Logs
echo.
echo Starting LidFlow...
start "" "%EXE%"

echo.
echo LidFlow is now running in the notification area.
echo Preview the effect with Ctrl+Alt+Shift+C ^(close^) and Ctrl+Alt+Shift+O ^(open^).
echo.
pause
exit /b 0
