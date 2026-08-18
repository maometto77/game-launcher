@echo off
rem Double-click wrapper for Uninstall-Don.ps1. See Install.cmd for why the
rem execution policy is bypassed here.

setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Uninstall-Don.ps1" %*

if errorlevel 1 (
    echo.
    echo Uninstall reported a problem. The details are above.
    pause
    exit /b 1
)

endlocal
