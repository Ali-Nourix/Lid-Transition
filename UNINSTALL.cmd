@echo off
REM ============================================================================
REM  LidFlow - uninstall
REM
REM  Removes the start-up entry, stops the app and deletes the installed files.
REM  Configuration and logs under %LOCALAPPDATA%\LidFlow are kept unless you
REM  confirm removal, so reinstalling keeps your tuning.
REM ============================================================================
setlocal EnableExtensions

set "TARGET_DIR=%LOCALAPPDATA%\Programs\LidFlow"
set "DATA_DIR=%LOCALAPPDATA%\LidFlow"

echo.
echo Uninstalling LidFlow
echo.

echo Removing the start-up entry...
reg delete "HKCU\Software\Microsoft\Windows\CurrentVersion\Run" /v LidFlow /f >nul 2>&1

tasklist /fi "imagename eq LidFlow.exe" 2>nul | find /i "LidFlow.exe" >nul
if %ERRORLEVEL% equ 0 (
    echo Stopping LidFlow...
    taskkill /im LidFlow.exe /f >nul 2>&1
    ping -n 3 127.0.0.1 >nul
)

if exist "%TARGET_DIR%" (
    echo Removing "%TARGET_DIR%"...

    REM This script may itself live in the folder being deleted, so it is removed
    REM from a detached command that outlives this one.
    if /i "%~dp0"=="%TARGET_DIR%\" (
        start "" /min cmd /c "ping -n 3 127.0.0.1 >nul & rmdir /s /q \"%TARGET_DIR%\""
        echo   Scheduled for removal.
    ) else (
        rmdir /s /q "%TARGET_DIR%"
    )
) else (
    echo No installed copy found at "%TARGET_DIR%".
)

echo.
set "REMOVE_DATA="
set /p "REMOVE_DATA=Also delete settings and logs in %DATA_DIR%? [y/N] "

if /i "%REMOVE_DATA%"=="y" (
    if exist "%DATA_DIR%" (
        rmdir /s /q "%DATA_DIR%"
        echo   Settings and logs deleted.
    )
) else (
    echo   Settings and logs kept.
)

echo.
echo LidFlow has been uninstalled.
echo.
pause
exit /b 0
