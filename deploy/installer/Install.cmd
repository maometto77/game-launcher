@echo off
rem Double-click wrapper for Install-Don.ps1.
rem
rem PowerShell refuses to run downloaded .ps1 files under the default execution
rem policy, and the error it gives says nothing about how to proceed. Rather than
rem telling people to change a machine-wide security setting, this bypasses the
rem policy for this one script and nothing else.

setlocal

powershell.exe -NoProfile -ExecutionPolicy Bypass -File "%~dp0Install-Don.ps1" -DesktopShortcut -Launch %*

if errorlevel 1 (
    echo.
    echo Install failed. The error is above.
    pause
    exit /b 1
)

endlocal
