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

    Finally it checks the returns ledger: the quantities and money cached on an invoice line must equal
    what its returns actually recorded, a return must add up to its own lines, and a restocked line must
    have put its units back through the stock ledger while a quarantined or destroyed one left the shelf
    alone.

    Supports the Milestone 2 acceptance criterion "stock balances reconcile with movements", the
    Milestone 3 invariant that every unit sold is accounted for by a stock movement, and the Milestone 4
    invariant that a return is traceable to its invoice and its ledger entries.

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
UNION ALL SELECT 'Payments', COUNT_BIG(*) FROM Payments
UNION ALL SELECT 'SalesReturns', COUNT_BIG(*) FROM SalesReturns
UNION ALL SELECT 'SalesReturnLines', COUNT_BIG(*) FROM SalesReturnLines;

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

# What an invoice line says has come back must be exactly what its return lines recorded, in units and in
# money, including the tax and the cost carried across with the refund.
$returnedLineMismatches = @'
SET NOCOUNT ON;
WITH Returned AS (
    SELECT SaleLineId,
           SUM(Quantity) AS Quantity,
           SUM(RefundAmount) AS RefundAmount,
           SUM(TaxAmount) AS TaxAmount,
           SUM(CostAmount) AS CostAmount
    FROM SalesReturnLines
    GROUP BY SaleLineId
)
SELECT l.Id AS SaleLineId, s.InvoiceNumber, l.LineNumber,
       l.ReturnedQuantity AS CachedQuantity, COALESCE(r.Quantity, 0) AS ReturnedQuantity,
       l.ReturnedAmount AS CachedAmount, COALESCE(r.RefundAmount, 0) AS RefundAmount,
       l.ReturnedTax AS CachedTax, COALESCE(r.TaxAmount, 0) AS TaxAmount,
       l.ReturnedCost AS CachedCost, COALESCE(r.CostAmount, 0) AS CostAmount
FROM SaleLines AS l
JOIN Sales AS s ON s.Id = l.SaleId
LEFT JOIN Returned AS r ON r.SaleLineId = l.Id
WHERE l.ReturnedQuantity <> COALESCE(r.Quantity, 0)
   OR l.ReturnedAmount <> COALESCE(r.RefundAmount, 0)
   OR l.ReturnedTax <> COALESCE(r.TaxAmount, 0)
   OR l.ReturnedCost <> COALESCE(r.CostAmount, 0);
'@

# A return's refund is what its lines say was given back, and every return belongs to an invoice.
$returnTotalMismatches = @'
SET NOCOUNT ON;
SELECT r.Id, r.ReturnNumber, r.SaleId, r.RefundTotal AS StoredRefund, SUM(l.RefundAmount) AS LineRefund
FROM SalesReturns AS r
JOIN SalesReturnLines AS l ON l.SalesReturnId = r.Id
GROUP BY r.Id, r.ReturnNumber, r.SaleId, r.RefundTotal
HAVING r.RefundTotal <> SUM(l.RefundAmount);

SELECT r.Id, r.ReturnNumber, r.SaleId
FROM SalesReturns AS r
WHERE NOT EXISTS (SELECT 1 FROM Sales AS s WHERE s.Id = r.SaleId)
   OR NOT EXISTS (SELECT 1 FROM SalesReturnLines AS l WHERE l.SalesReturnId = r.Id);
'@

# Only a restocked line puts units back, so the return movements must match the restocked lines exactly:
# one movement per return and batch, no movement for a line that was quarantined or destroyed, and no
# movement that no line accounts for. MovementType 3 is StockMovementType.SalesReturn and 1 is
# ReturnDisposition.Restock; quantities are aggregated because two restocked lines of one return may
# name the same batch.
$restockMismatches = @'
SET NOCOUNT ON;
WITH Restocked AS (
    SELECT l.SalesReturnId AS SalesReturnId, l.MedicineBatchId AS MedicineBatchId, SUM(l.Quantity) AS Quantity
    FROM SalesReturnLines AS l
    WHERE l.Disposition = 1
    GROUP BY l.SalesReturnId, l.MedicineBatchId
),
Moved AS (
    SELECT TRY_CONVERT(int, SUBSTRING(m.Reference, 5, 20)) AS SalesReturnId,
           m.MedicineBatchId AS MedicineBatchId,
           SUM(m.QuantityDelta) AS Quantity
    FROM StockMovements AS m
    WHERE m.MovementType = 3 AND m.Reference LIKE 'RET-%'
    GROUP BY TRY_CONVERT(int, SUBSTRING(m.Reference, 5, 20)), m.MedicineBatchId
)
SELECT COALESCE(restocked.SalesReturnId, moved.SalesReturnId) AS SalesReturnId,
       COALESCE(restocked.MedicineBatchId, moved.MedicineBatchId) AS MedicineBatchId,
       restocked.Quantity AS Restocked, moved.Quantity AS Moved
FROM Restocked AS restocked
FULL OUTER JOIN Moved AS moved
    ON moved.SalesReturnId = restocked.SalesReturnId
   AND moved.MedicineBatchId = restocked.MedicineBatchId
WHERE restocked.Quantity IS NULL
   OR moved.Quantity IS NULL
   OR restocked.Quantity <> moved.Quantity;
'@

$countsFile = Join-Path $env:TEMP 'medistock-counts.sql'
$reconFile = Join-Path $env:TEMP 'medistock-recon.sql'
$saleFile = Join-Path $env:TEMP 'medistock-sale-recon.sql'
$totalFile = Join-Path $env:TEMP 'medistock-total-recon.sql'
$returnedLineFile = Join-Path $env:TEMP 'medistock-returned-line-recon.sql'
$returnTotalFile = Join-Path $env:TEMP 'medistock-return-total-recon.sql'
$restockFile = Join-Path $env:TEMP 'medistock-restock-recon.sql'
$counts | Set-Content -Path $countsFile -Encoding ascii
$mismatches | Set-Content -Path $reconFile -Encoding ascii
$saleMismatches | Set-Content -Path $saleFile -Encoding ascii
$totalMismatches | Set-Content -Path $totalFile -Encoding ascii
$returnedLineMismatches | Set-Content -Path $returnedLineFile -Encoding ascii
$returnTotalMismatches | Set-Content -Path $returnTotalFile -Encoding ascii
$restockMismatches | Set-Content -Path $restockFile -Encoding ascii

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
Test-NoRows 'Invoice lines versus what their returns recorded' $returnedLineFile `
    'An invoice line and its returns disagree about how much came back or what it was worth.'
Test-NoRows 'Return headers versus their own lines' $returnTotalFile `
    'A return whose money does not add up to its lines, or which credits nothing, is stored in the database.'
Test-NoRows 'Restocked returns versus return movements' $restockFile `
    'The shelf and the returns ledger disagree about what was put back.'
