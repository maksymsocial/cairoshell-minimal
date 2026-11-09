# Cairo Minimal Taskbar - uninstaller
# Removes the app, autostart, Start menu shortcut and the Settings > Apps entry.
# Your settings and themes in %LOCALAPPDATA%\Cairo Desktop are kept unless you pass -RemoveSettings.

param([switch]$RemoveSettings)

$ErrorActionPreference = 'Continue'
$InstallDir = $PSScriptRoot
$AppName = 'Cairo Minimal Taskbar'

Write-Host '==> Closing the taskbar (Windows taskbar comes back)' -ForegroundColor Cyan
Get-Process CairoDesktop -ErrorAction SilentlyContinue | Where-Object { $_.Path -like "$InstallDir*" } | Stop-Process -Force

Write-Host '==> Removing autostart, shortcut and uninstall entry' -ForegroundColor Cyan
Remove-ItemProperty 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run' -Name 'CairoMinimal' -ErrorAction SilentlyContinue
Remove-Item (Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk") -ErrorAction SilentlyContinue
Remove-Item 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CairoMinimal' -Recurse -ErrorAction SilentlyContinue

if ($RemoveSettings) {
    Write-Host '==> Removing settings and themes' -ForegroundColor Cyan
    Remove-Item "$env:LOCALAPPDATA\Cairo Desktop" -Recurse -Force -ErrorAction SilentlyContinue
}

Write-Host "==> Deleting $InstallDir" -ForegroundColor Cyan
# this script lives in that folder, so delete it from a separate process after we exit
Start-Process cmd.exe -ArgumentList "/c timeout /t 2 /nobreak >nul & rmdir /s /q `"$InstallDir`"" -WindowStyle Hidden

Write-Host 'Uninstalled.' -ForegroundColor Green
