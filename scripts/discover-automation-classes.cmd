@echo off
where pwsh >nul 2>&1
if errorlevel 1 (
    echo ERROR: PowerShell 7+ ^(pwsh^) is required but not found on PATH.
    echo Install it from: https://aka.ms/powershell
    exit /b 1
)
pwsh -ExecutionPolicy Bypass -File "%~dp0discover-automation-classes-impl.ps1" %*
