<#
.SYNOPSIS
    Removes the MediStock application files and Start Menu shortcut.

.DESCRIPTION
    The database, backups, receipts, reports and logs are deliberately left behind - they are the
    pharmacy's records. The script prints where they are so you can remove them by hand if you
    really mean to.
#>
[CmdletBinding()]
param([string]$InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\MediStock'))

$ErrorActionPreference = 'Stop'

$shortcut = Join-Path ([Environment]::GetFolderPath('Programs')) 'MediStock.lnk'
if (Test-Path $shortcut) { Remove-Item $shortcut -Force }

if (Test-Path $InstallDir) {
    Get-Process MediStock.Desktop -ErrorAction SilentlyContinue |
        Where-Object { $_.Path -like "$InstallDir*" } | Stop-Process -Force
    # This script may be running from inside the folder; remove it after we exit.
    Start-Process powershell -WindowStyle Hidden -ArgumentList '-NoProfile', '-Command',
        "Start-Sleep 2; Remove-Item -LiteralPath '$InstallDir' -Recurse -Force"
}

Write-Host 'MediStock application files removed. Left in place on purpose:' -ForegroundColor Green
Write-Host '  database      MediStock on (localdb)\MSSQLLocalDB  (drop it with sqlcmd if you are sure)'
Write-Host "  documents     $([Environment]::GetFolderPath('MyDocuments'))\MediStock  (Backups, Receipts, Reports)"
Write-Host "  logs          $env:LOCALAPPDATA\MediStock\logs"
