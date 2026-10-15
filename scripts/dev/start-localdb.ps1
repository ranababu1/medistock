<#
.SYNOPSIS
    Starts the LocalDB instance MediStock develops against and verifies it answers queries.

.DESCRIPTION
    LocalDB is an on-demand engine: it does not run as a Windows service, so it must be started
    before the application or `dotnet ef` can connect. This script starts it, prints the instance
    details, and runs a trivial query to prove the connection works.

.EXAMPLE
    .\scripts\dev\start-localdb.ps1
#>
[CmdletBinding()]
param(
    [string] $Instance = 'MSSQLLocalDB'
)

$ErrorActionPreference = 'Stop'

if (-not (Get-Command sqllocaldb -ErrorAction SilentlyContinue)) {
    throw "sqllocaldb was not found on PATH. SQL Server Express LocalDB does not appear to be installed."
}

Write-Host "Starting LocalDB instance '$Instance'..." -ForegroundColor Cyan
sqllocaldb start $Instance | Out-Host

Write-Host ''
sqllocaldb info $Instance | Out-Host

Write-Host 'Verifying connectivity...' -ForegroundColor Cyan
$server = "(localdb)\$Instance"
& sqlcmd -S $server -E -b -Q "SELECT DB_NAME() AS CurrentDatabase, @@VERSION AS Version;" -W
if ($LASTEXITCODE -ne 0) {
    throw "Connected to sqllocaldb but sqlcmd could not query '$server'."
}

Write-Host ''
Write-Host "LocalDB is ready. Connection string:" -ForegroundColor Green
Write-Host "  Server=$server;Database=MediStock;Trusted_Connection=True;MultipleActiveResultSets=True;TrustServerCertificate=True"
