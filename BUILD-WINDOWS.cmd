@echo off
REM ============================================================================
REM  LidFlow - Windows build
REM
REM  Clone or extract the project and run this file. It checks prerequisites,
REM  restores packages, builds, tests, publishes and packages in one pass.
REM
REM  Requires the .NET 8 SDK (or newer) and nothing else. In particular it does
REM  NOT require the Windows SDK, fxc, or C++ build tools.
REM ============================================================================
setlocal

set "SCRIPT_DIR=%~dp0"
set "PS_SCRIPT=%SCRIPT_DIR%BUILD-WINDOWS.ps1"

if not exist "%PS_SCRIPT%" (
    echo.
    echo BUILD FAILED
    echo   BUILD-WINDOWS.ps1 was not found next to this file.
    echo   Extract the whole archive, not just BUILD-WINDOWS.cmd.
    echo.
    exit /b 1
)

REM Prefer PowerShell 7+ when present, fall back to the built-in Windows PowerShell.
where pwsh >nul 2>&1
if %ERRORLEVEL% equ 0 (
    pwsh -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %*
    goto :done
)

where powershell >nul 2>&1
if %ERRORLEVEL% neq 0 (
    echo.
    echo BUILD FAILED
    echo   Neither pwsh nor powershell was found on PATH.
    echo   Windows PowerShell ships with Windows; if it is missing, install
    echo   PowerShell 7 from https://aka.ms/powershell
    echo.
    exit /b 1
)

powershell -NoProfile -ExecutionPolicy Bypass -File "%PS_SCRIPT%" %*

:done
set "BUILD_EXIT=%ERRORLEVEL%"

REM Keep the window open when double-clicked from Explorer so the result is readable.
echo %CMDCMDLINE% | find /i "/c" >nul
if %ERRORLEVEL% equ 0 pause

exit /b %BUILD_EXIT%
