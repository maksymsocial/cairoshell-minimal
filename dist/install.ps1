# Cairo Minimal Taskbar - installer
# Installs for the current user only (no admin needed):
#   app       -> %LOCALAPPDATA%\Programs\CairoMinimal
#   autostart -> HKCU Run "CairoMinimal"
#   shortcut  -> Start menu "Cairo Minimal Taskbar"
#   uninstall -> Settings > Apps (or run uninstall.ps1 in the app folder)
#
# Usage: run install.bat, or in PowerShell:
#   irm https://github.com/maksymsocial/cairoshell-minimal/releases/latest/download/install.ps1 | iex

param(
    [string]$Zip,                     # use a local zip instead of downloading
    [string]$InstallDir = "$env:LOCALAPPDATA\Programs\CairoMinimal",
    [switch]$NoAutostart,
    [switch]$NoLaunch
)

$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'   # makes Invoke-WebRequest much faster on PowerShell 5
[Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12

$Repo    = 'maksymsocial/cairoshell-minimal'
$ZipUrl  = "https://github.com/$Repo/releases/latest/download/CairoMinimal-win-x64.zip"
$AppName = 'Cairo Minimal Taskbar'
$Exe     = Join-Path $InstallDir 'CairoDesktop.exe'

function Step($text) { Write-Host "==> $text" -ForegroundColor Cyan }

if (-not [Environment]::Is64BitOperatingSystem) { throw 'This build needs 64-bit Windows.' }
if ([Environment]::OSVersion.Version.Major -lt 10) { Write-Warning 'Made for Windows 10/11; older versions are untested.' }

$tmp = Join-Path $env:TEMP ("CairoMinimal-" + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force $tmp | Out-Null

try {
    if (-not $Zip) {
        Step 'Downloading the latest release'
        $Zip = Join-Path $tmp 'CairoMinimal.zip'
        Invoke-WebRequest -Uri $ZipUrl -OutFile $Zip -UseBasicParsing
    }

    Step 'Unpacking'
    $unpacked = Join-Path $tmp 'app'
    Expand-Archive -Path $Zip -DestinationPath $unpacked -Force
    # the zip may contain the files directly or inside one folder
    if (-not (Test-Path (Join-Path $unpacked 'CairoDesktop.exe'))) {
        $inner = Get-ChildItem $unpacked -Directory | Where-Object { Test-Path (Join-Path $_.FullName 'CairoDesktop.exe') } | Select-Object -First 1
        if (-not $inner) { throw 'CairoDesktop.exe not found in the zip.' }
        $unpacked = $inner.FullName
    }

    Step 'Closing a running copy (if any)'
    Get-Process CairoDesktop -ErrorAction SilentlyContinue | Stop-Process -Force
    Start-Sleep -Seconds 1

    Step "Installing to $InstallDir"
    if (Test-Path $InstallDir) { Remove-Item $InstallDir -Recurse -Force }
    New-Item -ItemType Directory -Force (Split-Path $InstallDir) | Out-Null
    Move-Item $unpacked $InstallDir
    Get-ChildItem $InstallDir -Recurse -File | Unblock-File

    $runKey = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Run'
    if (-not $NoAutostart) {
        Step 'Starting it with Windows'
        Set-ItemProperty $runKey -Name 'CairoMinimal' -Value "`"$Exe`""
    }

    # the official Cairo would start too and fight over the taskbar
    $official = Get-ItemProperty $runKey -ErrorAction SilentlyContinue
    foreach ($p in $official.PSObject.Properties) {
        if ($p.Name -ne 'CairoMinimal' -and "$($p.Value)" -match 'CairoDesktop\.exe' -and "$($p.Value)" -notlike "*$InstallDir*") {
            Write-Warning "Another Cairo starts with Windows ($($p.Name): $($p.Value)). Remove it or both will run."
        }
    }

    Step 'Adding a Start menu shortcut'
    $lnk = Join-Path ([Environment]::GetFolderPath('Programs')) "$AppName.lnk"
    $sh = New-Object -ComObject WScript.Shell
    $s = $sh.CreateShortcut($lnk)
    $s.TargetPath = $Exe
    $s.WorkingDirectory = $InstallDir
    $s.Description = $AppName
    $s.Save()

    Step 'Registering the uninstaller (Settings > Apps)'
    $u = 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Uninstall\CairoMinimal'
    New-Item -Path $u -Force | Out-Null
    $uninstall = "powershell.exe -NoProfile -ExecutionPolicy Bypass -File `"$InstallDir\uninstall.ps1`""
    Set-ItemProperty $u -Name DisplayName -Value $AppName
    Set-ItemProperty $u -Name DisplayIcon -Value $Exe
    Set-ItemProperty $u -Name Publisher -Value 'maksymsocial (Cairo fork)'
    Set-ItemProperty $u -Name InstallLocation -Value $InstallDir
    Set-ItemProperty $u -Name UninstallString -Value $uninstall
    Set-ItemProperty $u -Name URLInfoAbout -Value "https://github.com/$Repo"
    Set-ItemProperty $u -Name NoModify -Value 1 -Type DWord
    Set-ItemProperty $u -Name NoRepair -Value 1 -Type DWord
    $size = [int]((Get-ChildItem $InstallDir -Recurse -File | Measure-Object Length -Sum).Sum / 1KB)
    Set-ItemProperty $u -Name EstimatedSize -Value $size -Type DWord

    if (-not $NoLaunch) {
        Step 'Starting the taskbar'
        Start-Process -FilePath $Exe -WorkingDirectory $InstallDir
    }

    Write-Host ''
    Write-Host 'Done. Right-click the bar and choose "Taskbar settings..." to change it.' -ForegroundColor Green
    Write-Host "To remove it: Settings > Apps > $AppName, or run $InstallDir\uninstall.ps1"
}
finally {
    Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue
}
