<#
.SYNOPSIS
    Installs MediStock for the current user from an unzipped release folder.

.DESCRIPTION
    Copies the application to %LOCALAPPDATA%\Programs\MediStock (no administrator rights needed),
    creates a Start Menu shortcut, and checks that SQL Server LocalDB is available.

    Upgrading is the same command: the executable is replaced, while an existing appsettings.json
    and appsettings.Local.json are kept. If the shipped appsettings.json differs from yours, the new
    one is written beside it as appsettings.json.new for you to compare. The database, backups,
    receipts and logs are never touched.

.PARAMETER InstallDir
    Where to install. Defaults to %LOCALAPPDATA%\Programs\MediStock.

.EXAMPLE
    powershell -ExecutionPolicy Bypass -File .\install.ps1
#>
[CmdletBinding()]
param([string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\MediStock'))

$ErrorActionPreference = 'Stop'
$source = $PSScriptRoot
$exeName = 'MediStock.Desktop.exe'

if (-not (Test-Path (Join-Path $source $exeName))) {
    throw "$exeName was not found next to install.ps1. Unzip the release first and run the script from that folder."
}

if (Get-Process -Name ([IO.Path]::GetFileNameWithoutExtension($exeName)) -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -eq (Join-Path $InstallDir $exeName) }) {
    throw 'MediStock is running. Close it and run the installer again.'
}

New-Item -ItemType Directory -Force $InstallDir | Out-Null
$upgrade = Test-Path (Join-Path $InstallDir $exeName)

foreach ($file in Get-ChildItem $source -File) {
    if ($file.Name -in 'install.ps1', 'uninstall.ps1') { continue }

    $target = Join-Path $InstallDir $file.Name
    $isConfig = $file.Name -like 'appsettings*.json'

    if ($isConfig -and (Test-Path $target)) {
        if ((Get-FileHash $file.FullName).Hash -ne (Get-FileHash $target).Hash) {
            Copy-Item $file.FullName "$target.new" -Force
            Write-Host "Kept your $($file.Name); the shipped version is $($file.Name).new" -ForegroundColor Yellow
        }
        continue
    }
    Copy-Item $file.FullName $target -Force
}
Copy-Item (Join-Path $source 'uninstall.ps1') $InstallDir -Force -ErrorAction SilentlyContinue

$programs = [Environment]::GetFolderPath('Programs')
$shortcut = (New-Object -ComObject WScript.Shell).CreateShortcut((Join-Path $programs 'MediStock.lnk'))
$shortcut.TargetPath = Join-Path $InstallDir $exeName
$shortcut.WorkingDirectory = $InstallDir
$shortcut.Save()

Write-Host ("{0} MediStock in {1}" -f ($(if ($upgrade) { 'Upgraded' } else { 'Installed' }), $InstallDir)) -ForegroundColor Green

$localDb = Get-Command sqllocaldb -ErrorAction SilentlyContinue
if (-not $localDb) {
    Write-Host 'SQL Server LocalDB was not found. Install SQL Server Express LocalDB, or point appsettings.json at an existing SQL Server instance.' -ForegroundColor Yellow
}
else {
    sqllocaldb start MSSQLLocalDB | Out-Null
    Write-Host 'LocalDB instance MSSQLLocalDB is running.' -ForegroundColor Green
}
Write-Host 'Take a backup from Users & settings before upgrading a pharmacy that is in use.'
