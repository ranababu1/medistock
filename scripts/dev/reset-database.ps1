<#
.SYNOPSIS
    Drops and rebuilds the development database from EF Core migrations.

.DESCRIPTION
    Deletes the MediStock database, applies every migration from scratch and then launches the
    application briefly so the development seeder runs.

    This exists to prove the Milestone 1 acceptance criterion "migrations can create a fresh
    development database": the result is a database that came from migrations alone, with no
    hand-editing. Use it after a failed startup has left partial data behind.

.PARAMETER SkipSeed
    Rebuild the schema but do not seed sample data.

.EXAMPLE
    .\scripts\dev\reset-database.ps1
#>
[CmdletBinding()]
param(
    [string] $Connection = $env:MEDISTOCK_CONNECTION,
    [switch] $SkipSeed
)

$ErrorActionPreference = 'Stop'

$repoRoot = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
Push-Location $repoRoot
try {
    if ([string]::IsNullOrWhiteSpace($Connection)) {
        $Connection = 'Server=(localdb)\MSSQLLocalDB;Database=MediStock;Trusted_Connection=True;MultipleActiveResultSets=True;TrustServerCertificate=True'
        Write-Host 'No MEDISTOCK_CONNECTION set; using the LocalDB default.' -ForegroundColor DarkGray
        & "$PSScriptRoot\start-localdb.ps1" | Out-Null
    }
    $env:MEDISTOCK_CONNECTION = $Connection

    Write-Host 'Dropping the database...' -ForegroundColor Cyan
    dotnet ef database drop --force `
        --project src\MediStock.Infrastructure `
        --startup-project src\MediStock.Desktop | Out-Host
    if ($LASTEXITCODE -ne 0) {
        throw 'The database could not be dropped, so nothing was rebuilt.'
    }

    Write-Host 'Applying migrations...' -ForegroundColor Cyan
    dotnet ef database update `
        --project src\MediStock.Infrastructure `
        --startup-project src\MediStock.Desktop | Out-Host
    if ($LASTEXITCODE -ne 0) {
        # Without this the script used to announce success after a failed migration, which is exactly
        # the sort of thing a verification script must never do.
        throw 'A migration failed, so the database is not the schema this source describes.'
    }

    Write-Host 'Schema rebuilt from migrations alone.' -ForegroundColor Green

    if ($SkipSeed) {
        return
    }

    # Seeding is a side effect of application startup, not a migration. Running the app is the only
    # supported way to trigger it, so say that rather than pretending this script did it.
    Write-Host ''
    Write-Host 'The development seeder runs on next application start when the catalogue is empty:' -ForegroundColor Yellow
    Write-Host '  dotnet run --project src\MediStock.Desktop'
    Write-Host ''
    Write-Host 'Then verify the ledger with:' -ForegroundColor Yellow
    Write-Host '  .\scripts\dev\check-reconciliation.ps1'
}
finally {
    Pop-Location
}
