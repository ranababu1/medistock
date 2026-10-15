<#
.SYNOPSIS
    Verifies that every cached batch balance equals the sum of its stock movements.

.DESCRIPTION
    MediStock keeps a cached `MedicineBatches.QuantityOnHand` for query performance while treating
    the append-only `StockMovements` ledger as the source of truth. That duplication is only safe if
    the two agree, so this script compares them and fails loudly when they do not.

    It also checks the point-of-sale ledger: every batch allocation on an invoice must have a matching
    sale stock movement of the same size in the same batch, and vice versa. Then it prints the row
    counts and the inventory value at cost, which is the quickest way to confirm a fresh database was
    seeded.

    Supports the Milestone 2 acceptance criterion "stock balances reconcile with movements" and the
    Milestone 3 invariant that every unit sold is accounted for by a stock movement.

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
UNION ALL SELECT 'Movements', COUNT_BIG(*) FROM StockMovements
UNION ALL SELECT 'Sales', COUNT_BIG(*) FROM Sales
UNION ALL SELECT 'SaleLines', COUNT_BIG(*) FROM SaleLines
UNION ALL SELECT 'Allocations', COUNT_BIG(*) FROM SaleLineBatchAllocations
UNION ALL SELECT 'Payments', COUNT_BIG(*) FROM Payments;

SELECT CAST(COALESCE(SUM(QuantityOnHand * PurchaseCost), 0) AS decimal(18,2)) AS InventoryValueAtCost
FROM MedicineBatches WHERE QuantityOnHand > 0;

SELECT CAST(COALESCE(SUM(Total), 0) AS decimal(18,2)) AS TakingsOnRecord
FROM Sales WHERE Status <> 3;
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

# Quantities are aggregated per sale and batch because two lines of one invoice may draw on the same
# batch and produce a single movement between them. MovementType 2 is StockMovementType.Sale.
$saleMismatches = @'
SET NOCOUNT ON;
WITH Allocated AS (
    SELECT l.SaleId AS SaleId, a.MedicineBatchId AS MedicineBatchId, SUM(a.Quantity) AS Quantity
    FROM SaleLineBatchAllocations AS a
    JOIN SaleLines AS l ON l.Id = a.SaleLineId
    GROUP BY l.SaleId, a.MedicineBatchId
),
Moved AS (
    SELECT TRY_CONVERT(int, SUBSTRING(m.Reference, 6, 20)) AS SaleId,
           m.MedicineBatchId AS MedicineBatchId,
           SUM(-m.QuantityDelta) AS Quantity
    FROM StockMovements AS m
    WHERE m.MovementType = 2 AND m.Reference LIKE 'SALE-%'
    GROUP BY TRY_CONVERT(int, SUBSTRING(m.Reference, 6, 20)), m.MedicineBatchId
)
SELECT COALESCE(allocated.SaleId, moved.SaleId) AS SaleId,
       COALESCE(allocated.MedicineBatchId, moved.MedicineBatchId) AS MedicineBatchId,
       allocated.Quantity AS Allocated, moved.Quantity AS Moved
FROM Allocated AS allocated
FULL OUTER JOIN Moved AS moved
    ON moved.SaleId = allocated.SaleId AND moved.MedicineBatchId = allocated.MedicineBatchId
WHERE allocated.Quantity IS NULL
   OR moved.Quantity IS NULL
   OR allocated.Quantity <> moved.Quantity;
'@

# An invoice's own lines must add up to its stored totals.
$totalMismatches = @'
SET NOCOUNT ON;
SELECT s.Id, s.InvoiceNumber, s.Total AS StoredTotal, SUM(l.LineTotal) AS LineTotal
FROM Sales AS s
JOIN SaleLines AS l ON l.SaleId = s.Id
GROUP BY s.Id, s.InvoiceNumber, s.Total
HAVING s.Total <> SUM(l.LineTotal);
'@

$countsFile = Join-Path $env:TEMP 'medistock-counts.sql'
$reconFile = Join-Path $env:TEMP 'medistock-recon.sql'
$saleFile = Join-Path $env:TEMP 'medistock-sale-recon.sql'
$totalFile = Join-Path $env:TEMP 'medistock-total-recon.sql'
$counts | Set-Content -Path $countsFile -Encoding ascii
$mismatches | Set-Content -Path $reconFile -Encoding ascii
$saleMismatches | Set-Content -Path $saleFile -Encoding ascii
$totalMismatches | Set-Content -Path $totalFile -Encoding ascii

Write-Host "Inspecting $database on $server" -ForegroundColor Cyan
Write-Host ''

& sqlcmd -S $server -E -d $database -b -i $countsFile -W -s '|'
if ($LASTEXITCODE -ne 0) { throw "Could not read from '$database'." }

# Runs one expected-to-be-empty query and returns the offending rows.
function Test-NoRows {
    param([string] $Title, [string] $File, [string] $Failure)

    Write-Host ''
    Write-Host "${Title}:" -ForegroundColor Cyan
    $result = & sqlcmd -S $server -E -d $database -b -i $File -W -s '|' -h -1
    if ($LASTEXITCODE -ne 0) { throw "The '$Title' query failed." }

    $rows = @($result | Where-Object { $_ -and $_.Trim() -ne '' -and $_ -notmatch '^\(\d+ rows affected\)$' })
    if ($rows.Count -eq 0) {
        Write-Host 'OK' -ForegroundColor Green
        return
    }

    Write-Host 'MISMATCHES FOUND' -ForegroundColor Red
    $rows | Out-Host
    throw $Failure
}

Test-NoRows 'Cached balances versus the ledger' $reconFile `
    'Inventory does not reconcile: at least one batch has drifted from the ledger.'
Test-NoRows 'Invoice allocations versus sale movements' $saleFile `
    'An invoice and the stock ledger disagree about what left the shelf.'
Test-NoRows 'Stored invoice totals versus their lines' $totalFile `
    'An invoice whose lines do not add up to its total is stored in the database.'
