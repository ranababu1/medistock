<#
.SYNOPSIS
    Verifies that every cached batch balance equals the sum of its stock movements.

.DESCRIPTION
    MediStock keeps a cached `MedicineBatches.QuantityOnHand` for query performance while treating
    the append-only `StockMovements` ledger as the source of truth. That duplication is only safe if
    the two agree, so this script compares them and fails loudly when they do not.

    It also prints the row counts and the inventory value at cost, which is the quickest way to
    confirm a fresh database was seeded. Expected seed shape: 10 medicines, 4 suppliers, 8 purchases,
    13 purchase lines, 11 batches, 21 movements.

    Supports the Milestone 2 acceptance criterion "stock balances reconcile with movements".

.PARAMETER Connection
    Connection string to inspect. Defaults to the configured development database.

.EXAMPLE
    .\scripts\dev\check-reconciliation.ps1
#>
[CmdletBinding()]
param(
    [string] $Connection = $env:MEDISTOCK_CONNECTION
)

$ErrorActionPreference = 'Stop'

if ([string]::IsNullOrWhiteSpace($Connection)) {
    $Connection = 'Server=(localdb)\MSSQLLocalDB;Database=MediStock;Trusted_Connection=True;MultipleActiveResultSets=True;TrustServerCertificate=True'
}

# sqlcmd cannot take a .NET connection string, so translate the two parts we care about.
# The connection string is parsed by hand because neither SqlConnectionStringBuilder (System.Data.SqlClient)
# nor DbConnectionStringBuilder parses reliably under PowerShell 7.
function Get-ConnectionStringValue {
    param([string] $ConnectionString, [string[]] $Keys)

    foreach ($pair in $ConnectionString -split ';') {
        $pair = $pair.Trim()
        if ($pair.Length -eq 0) { continue }

        $separator = $pair.IndexOf('=')
        if ($separator -lt 1) {
            # SQL Server accepts a bare token as the data source, e.g. "(localdb)\MSSQLLocalDB;Database=X".
            if ($Keys -contains 'Server') { return $pair }
            continue
        }

        $name = $pair.Substring(0, $separator).Trim()
        if ($Keys -notcontains $name) { continue }

        return $pair.Substring($separator + 1).Trim().Trim('"', '{', '}')
    }

    return $null
}

$server = Get-ConnectionStringValue $Connection @('Data Source', 'Server', 'Address')
$database = Get-ConnectionStringValue $Connection @('Initial Catalog', 'Database')
if ([string]::IsNullOrWhiteSpace($server) -or [string]::IsNullOrWhiteSpace($database)) {
    throw "The connection string must name a server and a database. Got: $Connection"
}

$counts = @'
SET NOCOUNT ON;
SELECT 'Medicines' AS Entity, COUNT_BIG(*) FROM Medicines
UNION ALL SELECT 'Suppliers', COUNT_BIG(*) FROM Suppliers
UNION ALL SELECT 'Purchases', COUNT_BIG(*) FROM Purchases
UNION ALL SELECT 'PurchaseLines', COUNT_BIG(*) FROM PurchaseLines
UNION ALL SELECT 'Batches', COUNT_BIG(*) FROM MedicineBatches
UNION ALL SELECT 'Movements', COUNT_BIG(*) FROM StockMovements;

SELECT CAST(COALESCE(SUM(QuantityOnHand * PurchaseCost), 0) AS decimal(18,2)) AS InventoryValueAtCost
FROM MedicineBatches WHERE QuantityOnHand > 0;
'@

$mismatches = @'
SET NOCOUNT ON;
SELECT b.Id, b.BatchNumber, b.QuantityOnHand AS Cached, COALESCE(m.Total, 0) AS Ledger
FROM MedicineBatches AS b
OUTER APPLY (
    SELECT SUM(QuantityDelta) AS Total FROM StockMovements WHERE MedicineBatchId = b.Id
) AS m
WHERE b.QuantityOnHand <> COALESCE(m.Total, 0);
'@

$countsFile = Join-Path $env:TEMP 'medistock-counts.sql'
$reconFile = Join-Path $env:TEMP 'medistock-recon.sql'
$counts | Set-Content -Path $countsFile -Encoding ascii
$mismatches | Set-Content -Path $reconFile -Encoding ascii

Write-Host "Inspecting $database on $server" -ForegroundColor Cyan
Write-Host ''

& sqlcmd -S $server -E -d $database -b -i $countsFile -W -s '|'
if ($LASTEXITCODE -ne 0) { throw "Could not read from '$database'." }

Write-Host ''
Write-Host 'Reconciliation:' -ForegroundColor Cyan
$result = & sqlcmd -S $server -E -d $database -b -i $reconFile -W -s '|' -h -1
if ($LASTEXITCODE -ne 0) { throw 'The reconciliation query failed.' }

$rows = @($result | Where-Object { $_ -and $_.Trim() -ne '' -and $_ -notmatch '^\(\d+ rows affected\)$' })
if ($rows.Count -eq 0) {
    Write-Host 'OK - every cached balance equals its ledger sum.' -ForegroundColor Green
}
else {
    Write-Host 'MISMATCHES FOUND' -ForegroundColor Red
    $rows | Out-Host
    throw "Inventory does not reconcile: $($rows.Count) batch(es) drifted from the ledger."
}
