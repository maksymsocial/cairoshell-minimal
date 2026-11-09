@echo off
rem Cairo Minimal Taskbar - double-click to install (no admin needed).
rem Uses install.ps1 next to this file, or downloads it from the latest GitHub release.
setlocal
set "PS1=%~dp0install.ps1"
if exist "%PS1%" (
    powershell -NoProfile -ExecutionPolicy Bypass -File "%PS1%"
) else (
    powershell -NoProfile -ExecutionPolicy Bypass -Command "[Net.ServicePointManager]::SecurityProtocol='Tls12'; $ProgressPreference='SilentlyContinue'; $f=Join-Path $env:TEMP 'CairoMinimal-install.ps1'; Invoke-WebRequest 'https://github.com/maksymsocial/cairoshell-minimal/releases/latest/download/install.ps1' -OutFile $f -UseBasicParsing; & $f; Remove-Item $f"
)
echo.
pause
