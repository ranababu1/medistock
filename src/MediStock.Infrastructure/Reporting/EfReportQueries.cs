using MediStock.Application.Common;
using MediStock.Application.Reporting;
using MediStock.Domain.Common;
using MediStock.Domain.Inventory;
using MediStock.Domain.Sales;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MediStock.Infrastructure.Reporting;

/// <summary>
/// Entity Framework Core queries behind the ten reports.
/// </summary>
/// <remarks>
/// <para>
/// Every figure is summed from rows that were written when the transaction happened — invoice totals,
/// stored line amounts, movements and batch balances — and is never recomputed from prices or quantities
/// the document did not record. That is what lets a report be reconciled with the transaction behind it.
/// </para>
/// <para>
/// Aggregates are grouped in SQL and re-projected in memory, because SqlServer cannot translate a record
/// constructor inside <c>GroupBy</c>. Money is summed as stored and rounded once with
/// <see cref="Money.Round"/>; nothing here invents a second rounding rule of its own.
/// </para>
/// <para>
/// Where a report mixes sales with returns, tax and cost are shown net of what came back, so
/// <c>margin = net - tax - cost</c> holds on every row and on the totals row. Wording comes from
/// <see cref="ReportText"/> in one place, so the screen and the exported file agree.
/// </para>
/// </remarks>
internal sealed class EfReportQueries : IReportQueries
{
    /// <summary>Shown on a batch whose supplier was not recorded, rather than an empty cell.</summary>
    private const string NoSupplier = "(none)";

    private static readonly ReportColumn[] DailySalesColumns =
    [
        new("Date"),
        new("Invoices", IsNumeric: true),
        new("Units sold", IsNumeric: true),
        new("Units returned", IsNumeric: true),
        new("Gross", IsNumeric: true),
        new("Discount", IsNumeric: true),
        new("Tax", IsNumeric: true),
        new("Sales", IsNumeric: true),
        new("Returns", IsNumeric: true),
        new("Refunds", IsNumeric: true),
        new("Net", IsNumeric: true),
        new("Cost", IsNumeric: true),
        new("Margin", IsNumeric: true),
    ];

    private static readonly ReportColumn[] SalesByMedicineColumns =
    [
        new("Medicine"),
        new("Units sold", IsNumeric: true),
        new("Units returned", IsNumeric: true),
        new("Gross", IsNumeric: true),
        new("Discount", IsNumeric: true),
        new("Tax", IsNumeric: true),
        new("Sales", IsNumeric: true),
        new("Refunds", IsNumeric: true),
        new("Net", IsNumeric: true),
        new("Cost", IsNumeric: true),
        new("Margin", IsNumeric: true),
    ];

    private static readonly ReportColumn[] SalesByPaymentMethodColumns =
    [
        new("Payment method"),
        new("Invoices", IsNumeric: true),
        new("Taken", IsNumeric: true),
        new("Refunds", IsNumeric: true),
        new("Net", IsNumeric: true),
    ];

    private static readonly ReportColumn[] PurchaseHistoryColumns =
    [
        new("Received"),
        new("Supplier"),
        new("Invoice"),
        new("Medicine"),
        new("Batch"),
        new("Expiry"),
        new("Units", IsNumeric: true),
        new("Unit cost", IsNumeric: true),
        new("Value", IsNumeric: true),
    ];

    private static readonly ReportColumn[] StockByBatchColumns =
    [
        new("Medicine"),
        new("Batch"),
        new("Expiry"),
        new("Days", IsNumeric: true),
        new("Units on hand", IsNumeric: true),
        new("Unit cost", IsNumeric: true),
        new("Value at cost", IsNumeric: true),
        new("Dispensable"),
    ];

    private static readonly ReportColumn[] LowStockColumns =
    [
        new("Medicine"),
        new("Strength"),
        new("On hand", IsNumeric: true),
        new("Threshold", IsNumeric: true),
        new("To reorder", IsNumeric: true),
        new("Nearest expiry"),
        new("Batches", IsNumeric: true),
    ];

    private static readonly ReportColumn[] ExpiringStockColumns =
    [
        new("Medicine"),
        new("Batch"),
        new("Expiry"),
        new("Days", IsNumeric: true),
        new("Units", IsNumeric: true),
        new("Value at cost", IsNumeric: true),
        new("Status"),
    ];

    private static readonly ReportColumn[] StockMovementColumns =
    [
        new("Date"),
        new("Movement"),
        new("Medicine"),
        new("Batch"),
        new("Change", IsNumeric: true),
        new("Balance after", IsNumeric: true),
        new("Operator"),
        new("Reason"),
        new("Reference"),
    ];

    private static readonly ReportColumn[] InventoryValuationColumns =
    [
        new("Medicine"),
        new("Batches", IsNumeric: true),
        new("Units", IsNumeric: true),
        new("Value at cost", IsNumeric: true),
        new("Value at retail", IsNumeric: true),
        new("Potential margin", IsNumeric: true),
    ];

    private static readonly ReportColumn[] SalesReturnColumns =
    [
        new("Date"),
        new("Return"),
        new("Invoice"),
        new("Medicine"),
        new("Batch"),
        new("Units", IsNumeric: true),
        new("Refund", IsNumeric: true),
        new("Disposition"),
        new("Operator"),
        new("Reason"),
    ];

    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IClock _clock;

    public EfReportQueries(IDbContextFactory<MediStockDbContext> factory, IClock clock)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<ReportTable> RunAsync(
        ReportRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        // Normalising here rather than inside each report means they all see the same parameters, and the
        // subtitle a report prints is the range it actually used.
        var asked = request.Normalise(_clock.BusinessDate);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return asked.Kind switch
        {
            ReportKind.DailySales => await DailySalesAsync(db, asked, cancellationToken).ConfigureAwait(false),
            ReportKind.SalesByMedicine => await SalesByMedicineAsync(db, asked, cancellationToken).ConfigureAwait(false),
            ReportKind.SalesByPaymentMethod => await SalesByPaymentMethodAsync(db, asked, cancellationToken).ConfigureAwait(false),
            ReportKind.PurchaseHistory => await PurchaseHistoryAsync(db, asked, cancellationToken).ConfigureAwait(false),
            ReportKind.StockByBatch => await StockByBatchAsync(db, asked, cancellationToken).ConfigureAwait(false),
            ReportKind.LowStock => await LowStockAsync(db, asked, cancellationToken).ConfigureAwait(false),
            ReportKind.ExpiringStock => await ExpiringStockAsync(db, asked, cancellationToken).ConfigureAwait(false),
            ReportKind.StockMovements => await StockMovementsAsync(db, asked, cancellationToken).ConfigureAwait(false),
            ReportKind.InventoryValuation => await InventoryValuationAsync(db, asked, cancellationToken).ConfigureAwait(false),
            ReportKind.SalesReturns => await SalesReturnsAsync(db, asked, cancellationToken).ConfigureAwait(false),
            _ => throw new ArgumentOutOfRangeException(nameof(request), asked.Kind, "There is no such report."),
        };
    }

    /// <summary>One row per trading day: what was invoiced, what came back, and what was left.</summary>
    private static async Task<ReportTable> DailySalesAsync(
        MediStockDbContext db,
        ReportRequest request,
        CancellationToken cancellationToken)
    {
        var from = request.From!.Value;
        var to = request.To!.Value;

        var sales = await db.Sales
            .AsNoTracking()
            .Where(sale => sale.BusinessDate >= from
                && sale.BusinessDate <= to
                && sale.Status != SaleStatus.Cancelled)
            .GroupBy(sale => sale.BusinessDate)
            .Select(group => new
            {
                BusinessDate = group.Key,
                InvoiceCount = group.Count(),
                SubTotal = group.Sum(sale => sale.SubTotal),
                DiscountTotal = group.Sum(sale => sale.DiscountTotal),
                TaxTotal = group.Sum(sale => sale.TaxTotal),
                Total = group.Sum(sale => sale.Total),
                CostTotal = group.Sum(sale => sale.CostTotal),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Quantities live on the lines rather than the header, so they need their own aggregate.
        var unitsSold = await db.SaleLines
            .AsNoTracking()
            .Where(line => line.Sale!.BusinessDate >= from
                && line.Sale.BusinessDate <= to
                && line.Sale.Status != SaleStatus.Cancelled)
            .GroupBy(line => line.Sale!.BusinessDate)
            .Select(group => new { BusinessDate = group.Key, Units = group.Sum(line => line.Quantity) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var returns = await db.SalesReturns
            .AsNoTracking()
            .Where(salesReturn => salesReturn.BusinessDate >= from && salesReturn.BusinessDate <= to)
            .GroupBy(salesReturn => salesReturn.BusinessDate)
            .Select(group => new
            {
                BusinessDate = group.Key,
                ReturnCount = group.Count(),
                RefundTotal = group.Sum(salesReturn => salesReturn.RefundTotal),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var returnedLines = await db.SalesReturnLines
            .AsNoTracking()
            .Where(line => line.SalesReturn.BusinessDate >= from && line.SalesReturn.BusinessDate <= to)
            .GroupBy(line => line.SalesReturn.BusinessDate)
            .Select(group => new
            {
                BusinessDate = group.Key,
                Units = group.Sum(line => line.Quantity),
                TaxAmount = group.Sum(line => line.TaxAmount),
                CostAmount = group.Sum(line => line.CostAmount),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // A day that saw only refunds is still a day the till has to account for, so the dates are the
        // union of the two sides rather than only the days that had sales.
        var dates = sales
            .Select(row => row.BusinessDate)
            .Concat(returns.Select(row => row.BusinessDate))
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        if (dates.Count == 0)
        {
            return ReportTable.Empty(
                ReportDefinitions.For(request.Kind).Title,
                request.Describe(),
                DailySalesColumns);
        }

        var salesByDate = sales.ToDictionary(row => row.BusinessDate);
        var unitsByDate = unitsSold.ToDictionary(row => row.BusinessDate, row => row.Units);
        var returnsByDate = returns.ToDictionary(row => row.BusinessDate);
        var returnedByDate = returnedLines.ToDictionary(row => row.BusinessDate);

        var rows = new List<IReadOnlyList<object?>>(dates.Count + 1);

        foreach (var date in dates)
        {
            salesByDate.TryGetValue(date, out var sold);
            returnsByDate.TryGetValue(date, out var refunded);
            returnedByDate.TryGetValue(date, out var cameBack);

            var tax = (sold?.TaxTotal ?? 0m) - (cameBack?.TaxAmount ?? 0m);
            var cost = (sold?.CostTotal ?? 0m) - (cameBack?.CostAmount ?? 0m);
            var net = (sold?.Total ?? 0m) - (refunded?.RefundTotal ?? 0m);

            rows.Add(new object?[]
            {
                date,
                sold?.InvoiceCount ?? 0,
                unitsByDate.TryGetValue(date, out var units) ? units : 0m,
                cameBack?.Units ?? 0m,
                sold?.SubTotal ?? 0m,
                sold?.DiscountTotal ?? 0m,
                Money.Round(tax),
                sold?.Total ?? 0m,
                refunded?.ReturnCount ?? 0,
                refunded?.RefundTotal ?? 0m,
                Money.Round(net),
                Money.Round(cost),
                Money.Round(net - tax - cost),
            });
        }

        AddTotals(rows, "Total", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11, 12);

        return new ReportTable(
            ReportDefinitions.For(request.Kind).Title,
            request.Describe(),
            DailySalesColumns,
            rows);
    }

    /// <summary>What each medicine sold, what came back, and what it earned.</summary>
    private static async Task<ReportTable> SalesByMedicineAsync(
        MediStockDbContext db,
        ReportRequest request,
        CancellationToken cancellationToken)
    {
        var from = request.From!.Value;
        var to = request.To!.Value;

        // The name the batch was charged under is the one the invoice shows, so a later rename in the
        // catalogue never rewrites what an old report says was sold.
        var sold = await Range(db.SaleLines, from, to)
            .GroupBy(line => new { line.MedicineId, line.MedicineName })
            .Select(group => new
            {
                group.Key.MedicineId,
                group.Key.MedicineName,
                Units = group.Sum(line => line.Quantity),
                GrossAmount = group.Sum(line => line.GrossAmount),
                DiscountAmount = group.Sum(line => line.DiscountAmount),
                TaxAmount = group.Sum(line => line.TaxAmount),
                Total = group.Sum(line => line.LineTotal),
                CostTotal = group.Sum(line => line.Quantity * line.UnitCost),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Returns are grouped through the invoice line they credit, which is where the medicine is named.
        var returned = await db.SalesReturnLines
            .AsNoTracking()
            .Where(line => line.SalesReturn.BusinessDate >= from && line.SalesReturn.BusinessDate <= to)
            .GroupBy(line => new { line.SaleLine.MedicineId, line.SaleLine.MedicineName })
            .Select(group => new
            {
                group.Key.MedicineId,
                group.Key.MedicineName,
                Units = group.Sum(line => line.Quantity),
                RefundTotal = group.Sum(line => line.RefundAmount),
                TaxAmount = group.Sum(line => line.TaxAmount),
                CostAmount = group.Sum(line => line.CostAmount),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var soldByMedicine = sold.ToDictionary(row => (row.MedicineId, row.MedicineName));
        var returnedByMedicine = returned.ToDictionary(row => (row.MedicineId, row.MedicineName));

        var medicines = soldByMedicine.Keys
            .Concat(returnedByMedicine.Keys)
            .Distinct()
            .OrderBy(key => key.MedicineName, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(key => key.MedicineId)
            .ToList();

        var rows = new List<IReadOnlyList<object?>>(medicines.Count + 1);

        foreach (var medicine in medicines)
        {
            soldByMedicine.TryGetValue(medicine, out var invoiced);
            returnedByMedicine.TryGetValue(medicine, out var refunded);

            var tax = (invoiced?.TaxAmount ?? 0m) - (refunded?.TaxAmount ?? 0m);
            var cost = (invoiced?.CostTotal ?? 0m) - (refunded?.CostAmount ?? 0m);
            var net = (invoiced?.Total ?? 0m) - (refunded?.RefundTotal ?? 0m);

            rows.Add(new object?[]
            {
                medicine.MedicineName,
                invoiced?.Units ?? 0m,
                refunded?.Units ?? 0m,
                invoiced?.GrossAmount ?? 0m,
                invoiced?.DiscountAmount ?? 0m,
                Money.Round(tax),
                invoiced?.Total ?? 0m,
                refunded?.RefundTotal ?? 0m,
                Money.Round(net),
                Money.Round(cost),
                Money.Round(net - tax - cost),
            });
        }

        AddTotals(rows, "Total", 1, 2, 3, 4, 5, 6, 7, 8, 9, 10);

        return new ReportTable(
            ReportDefinitions.For(request.Kind).Title,
            request.Describe(),
            SalesByMedicineColumns,
            rows);
    }

    /// <summary>What was taken and refunded through each way of paying.</summary>
    private static async Task<ReportTable> SalesByPaymentMethodAsync(
        MediStockDbContext db,
        ReportRequest request,
        CancellationToken cancellationToken)
    {
        var from = request.From!.Value;
        var to = request.To!.Value;

        var taken = await Range(db.Sales, from, to)
            .SelectMany(sale => sale.Payments)
            .GroupBy(payment => payment.Method)
            .Select(group => new
            {
                Method = group.Key,
                InvoiceCount = group.Select(payment => payment.SaleId).Distinct().Count(),
                Amount = group.Sum(payment => payment.Amount),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Refunds are netted off the method the money came back through, because that is the drawer or
        // the terminal the cashier has to reconcile at the end of the day.
        var refunded = await db.SalesReturns
            .AsNoTracking()
            .Where(salesReturn => salesReturn.BusinessDate >= from && salesReturn.BusinessDate <= to)
            .GroupBy(salesReturn => salesReturn.RefundMethod)
            .Select(group => new { Method = group.Key, Amount = group.Sum(row => row.RefundTotal) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<IReadOnlyList<object?>>(4);

        // Enum order, so the report reads the same way every time, and a method that saw no movement at
        // all does not appear as an empty line.
        foreach (var method in Enum.GetValues<PaymentMethod>())
        {
            var invoiced = taken.Find(row => row.Method == method);
            var refunds = refunded.Find(row => row.Method == method);

            if (invoiced is null && refunds is null)
            {
                continue;
            }

            rows.Add(new object?[]
            {
                method,
                invoiced?.InvoiceCount ?? 0,
                invoiced?.Amount ?? 0m,
                refunds?.Amount ?? 0m,
                Money.Round((invoiced?.Amount ?? 0m) - (refunds?.Amount ?? 0m)),
            });
        }

        AddTotals(rows, "Total", 1, 2, 3, 4);

        return new ReportTable(
            ReportDefinitions.For(request.Kind).Title,
            request.Describe(),
            SalesByPaymentMethodColumns,
            rows);
    }

    /// <summary>Every batch received in the range, with the supplier invoice it arrived on.</summary>
    private static async Task<ReportTable> PurchaseHistoryAsync(
        MediStockDbContext db,
        ReportRequest request,
        CancellationToken cancellationToken)
    {
        var from = request.From!.Value;
        var to = request.To!.Value;

        var batches = await db.MedicineBatches
            .AsNoTracking()
            .Where(batch => batch.ReceivedOn >= from && batch.ReceivedOn <= to)
            .OrderBy(batch => batch.ReceivedOn)
            .ThenBy(batch => batch.Id)
            .Select(batch => new
            {
                batch.ReceivedOn,
                SupplierName = batch.Supplier != null ? batch.Supplier.Name : NoSupplier,
                batch.SupplierInvoiceNumber,
                MedicineName = batch.Medicine.DisplayName,
                batch.BatchNumber,
                batch.ExpiryDate,
                batch.QuantityReceived,
                batch.PurchaseCost,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = batches
            .Select(batch => (IReadOnlyList<object?>)new object?[]
            {
                batch.ReceivedOn,
                batch.SupplierName,
                batch.SupplierInvoiceNumber ?? string.Empty,
                batch.MedicineName,
                batch.BatchNumber,
                batch.ExpiryDate,
                batch.QuantityReceived,
                batch.PurchaseCost,
                Money.Round(batch.QuantityReceived * batch.PurchaseCost),
            })
            .ToList();

        // Unit cost is left out of the totals: adding unit prices together says nothing, whereas the units
        // received and the value of them do.
        AddTotals(rows, "Total", 6, 8);

        return new ReportTable(
            ReportDefinitions.For(request.Kind).Title,
            request.Describe(),
            PurchaseHistoryColumns,
            rows);
    }

    /// <summary>Every batch still holding stock, oldest expiry first.</summary>
    private static async Task<ReportTable> StockByBatchAsync(
        MediStockDbContext db,
        ReportRequest request,
        CancellationToken cancellationToken)
    {
        var businessDate = request.To!.Value;

        var batches = await db.MedicineBatches
            .AsNoTracking()
            .Where(batch => batch.QuantityOnHand > 0)
            .OrderBy(batch => batch.ExpiryDate)
            .ThenBy(batch => batch.Medicine.DisplayName)
            .ThenBy(batch => batch.BatchNumber)
            .Select(batch => new
            {
                MedicineName = batch.Medicine.DisplayName,
                batch.BatchNumber,
                batch.ExpiryDate,
                batch.QuantityOnHand,
                batch.PurchaseCost,
                batch.Status,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<IReadOnlyList<object?>>(batches.Count + 1);

        foreach (var batch in batches)
        {
            rows.Add(new object?[]
            {
                batch.MedicineName,
                batch.BatchNumber,
                batch.ExpiryDate,

                // DayNumber arithmetic has no SQL translation, so the age of an expiry is worked out here.
                batch.ExpiryDate.DayNumber - businessDate.DayNumber,
                batch.QuantityOnHand,
                batch.PurchaseCost,
                Money.Round(batch.QuantityOnHand * batch.PurchaseCost),

                // Eligibility is asked of ExpiryPolicy rather than restated, so the report can never
                // disagree with the till about what may be dispensed.
                ExpiryPolicy.IsSellable(batch.Status, batch.ExpiryDate, batch.QuantityOnHand, businessDate),
            });
        }

        AddTotals(rows, "Total", 4, 6);

        return new ReportTable(
            ReportDefinitions.For(request.Kind).Title,
            request.Describe(),
            StockByBatchColumns,
            rows);
    }

    /// <summary>Medicines at or below their reorder threshold, worst shortage first.</summary>
    private static async Task<ReportTable> LowStockAsync(
        MediStockDbContext db,
        ReportRequest request,
        CancellationToken cancellationToken)
    {
        var businessDate = request.To!.Value;

        var medicines = await db.Medicines
            .AsNoTracking()
            .Where(medicine => medicine.IsActive && medicine.ReorderThreshold != null)
            .Select(medicine => new
            {
                medicine.Id,
                medicine.DisplayName,
                medicine.Strength,
                Threshold = medicine.ReorderThreshold!.Value,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (medicines.Count == 0)
        {
            return ReportTable.Empty(
                ReportDefinitions.For(request.Kind).Title,
                request.Describe(),
                LowStockColumns);
        }

        // Only dispensable stock counts towards being in stock: a shelf of expired packs is not a medicine
        // a pharmacist can hand over. The dashboard applies the same rule.
        var batches = await db.MedicineBatches
            .AsNoTracking()
            .Where(batch => batch.Status == BatchStatus.Active
                && batch.ExpiryDate >= businessDate
                && batch.QuantityOnHand > 0)
            .Select(batch => new { batch.MedicineId, batch.QuantityOnHand, batch.ExpiryDate })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byMedicine = batches
            .GroupBy(batch => batch.MedicineId)
            .ToDictionary(
                group => group.Key,
                group => (
                    Quantity: group.Sum(batch => batch.QuantityOnHand),
                    BatchCount: group.Count(),
                    NearestExpiry: group.Min(batch => batch.ExpiryDate)));

        var rows = new List<IReadOnlyList<object?>>();

        foreach (var medicine in medicines)
        {
            byMedicine.TryGetValue(medicine.Id, out var balance);

            if (balance.Quantity > medicine.Threshold)
            {
                continue;
            }

            rows.Add(new object?[]
            {
                medicine.DisplayName,
                medicine.Strength ?? string.Empty,
                balance.Quantity,
                medicine.Threshold,
                Money.Round(medicine.Threshold - balance.Quantity),
                balance.BatchCount == 0 ? null : balance.NearestExpiry,
                balance.BatchCount,
            });
        }

        rows.Sort((left, right) =>
        {
            var shortage = ((decimal)right[4]!).CompareTo((decimal)left[4]!);
            return shortage != 0
                ? shortage
                : string.Compare((string)left[0]!, (string)right[0]!, StringComparison.CurrentCultureIgnoreCase);
        });

        return new ReportTable(
            ReportDefinitions.For(request.Kind).Title,
            request.Describe(),
            LowStockColumns,
            rows);
    }

    /// <summary>Stock expiring within the horizon, and stock whose expiry has already passed.</summary>
    private static async Task<ReportTable> ExpiringStockAsync(
        MediStockDbContext db,
        ReportRequest request,
        CancellationToken cancellationToken)
    {
        var businessDate = request.To!.Value;
        var horizon = request.HorizonDays!.Value;
        var limit = businessDate.AddDays(horizon);

        var batches = await db.MedicineBatches
            .AsNoTracking()
            .Where(batch => batch.QuantityOnHand > 0
                && batch.Status != BatchStatus.Depleted
                && batch.ExpiryDate <= limit)
            .OrderBy(batch => batch.ExpiryDate)
            .ThenBy(batch => batch.BatchNumber)
            .Select(batch => new
            {
                MedicineName = batch.Medicine.DisplayName,
                batch.BatchNumber,
                batch.ExpiryDate,
                batch.QuantityOnHand,
                batch.PurchaseCost,
                batch.Status,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<IReadOnlyList<object?>>(batches.Count + 1);

        foreach (var batch in batches)
        {
            var days = batch.ExpiryDate.DayNumber - businessDate.DayNumber;

            rows.Add(new object?[]
            {
                batch.MedicineName,
                batch.BatchNumber,
                batch.ExpiryDate,
                days,
                batch.QuantityOnHand,
                Money.Round(batch.QuantityOnHand * batch.PurchaseCost),

                // A batch nobody has swept yet still carries its original status, so an expiry that has
                // passed is reported as expired whether or not it has been written off.
                days < 0 && batch.Status == BatchStatus.Active ? BatchStatus.Expired : batch.Status,
            });
        }

        AddTotals(rows, "Total", 4, 5);

        return new ReportTable(
            ReportDefinitions.For(request.Kind).Title,
            $"Expiring on or before {limit:yyyy-MM-dd}, as of {businessDate:yyyy-MM-dd}",
            ExpiringStockColumns,
            rows);
    }

    /// <summary>The stock ledger over a range, newest first.</summary>
    private static async Task<ReportTable> StockMovementsAsync(
        MediStockDbContext db,
        ReportRequest request,
        CancellationToken cancellationToken)
    {
        var from = request.From!.Value;
        var to = request.To!.Value;
        var limit = request.Take!.Value;

        var movements = await db.StockMovements
            .AsNoTracking()
            .Where(movement => movement.BusinessDate >= from && movement.BusinessDate <= to)
            .OrderByDescending(movement => movement.OccurredAtUtc)
            .ThenByDescending(movement => movement.Id)
            .Take(limit)
            .Select(movement => new
            {
                movement.BusinessDate,
                movement.MovementType,
                MedicineName = movement.MedicineBatch.Medicine.DisplayName,
                movement.MedicineBatch.BatchNumber,
                movement.QuantityDelta,
                movement.QuantityAfter,
                movement.PerformedBy,
                movement.Reason,
                movement.Reference,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = movements
            .Select(movement => (IReadOnlyList<object?>)new object?[]
            {
                movement.BusinessDate,
                movement.MovementType,
                movement.MedicineName,
                movement.BatchNumber,
                movement.QuantityDelta,
                movement.QuantityAfter,
                movement.PerformedBy,
                movement.Reason ?? string.Empty,
                movement.Reference ?? string.Empty,
            })
            .ToList();

        // A ledger has no total worth printing — the balance after each movement is what matters — but a
        // truncated ledger must say so, or the reader would take it for the whole history.
        var subtitle = movements.Count == limit
            ? $"{request.Describe()} (most recent {limit} rows)"
            : request.Describe();

        return new ReportTable(
            ReportDefinitions.For(request.Kind).Title,
            subtitle,
            StockMovementColumns,
            rows);
    }

    /// <summary>What the stock on hand cost and what it would sell for, by medicine.</summary>
    private static async Task<ReportTable> InventoryValuationAsync(
        MediStockDbContext db,
        ReportRequest request,
        CancellationToken cancellationToken)
    {
        var batches = await db.MedicineBatches
            .AsNoTracking()
            .Where(batch => batch.QuantityOnHand > 0)
            .Select(batch => new
            {
                MedicineName = batch.Medicine.DisplayName,
                batch.QuantityOnHand,
                batch.PurchaseCost,
                batch.SellingPrice,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = batches
            .GroupBy(batch => batch.MedicineName)
            .OrderBy(group => group.Key, StringComparer.CurrentCultureIgnoreCase)
            .Select(group =>
            {
                var units = group.Sum(batch => batch.QuantityOnHand);
                var cost = Money.Round(group.Sum(batch => batch.QuantityOnHand * batch.PurchaseCost));
                var retail = Money.Round(group.Sum(batch => batch.QuantityOnHand * batch.SellingPrice));

                return (IReadOnlyList<object?>)new object?[]
                {
                    group.Key,
                    group.Count(),
                    units,
                    cost,
                    retail,
                    Money.Round(retail - cost),
                };
            })
            .ToList();

        AddTotals(rows, "Total", 1, 2, 3, 4, 5);

        return new ReportTable(
            ReportDefinitions.For(request.Kind).Title,
            request.Describe(),
            InventoryValuationColumns,
            rows);
    }

    /// <summary>Customer returns, one row per returned quantity, with where the goods went.</summary>
    private static async Task<ReportTable> SalesReturnsAsync(
        MediStockDbContext db,
        ReportRequest request,
        CancellationToken cancellationToken)
    {
        var from = request.From!.Value;
        var to = request.To!.Value;

        var lines = await db.SalesReturnLines
            .AsNoTracking()
            .Where(line => line.SalesReturn.BusinessDate >= from && line.SalesReturn.BusinessDate <= to)
            .OrderByDescending(line => line.SalesReturn.BusinessDate)
            .ThenByDescending(line => line.SalesReturnId)
            .ThenBy(line => line.LineNumber)
            .Select(line => new
            {
                line.SalesReturn.BusinessDate,
                line.SalesReturn.ReturnNumber,
                InvoiceNumber = line.SalesReturn.Sale.InvoiceNumber,
                MedicineName = line.SaleLine.MedicineName,
                BatchNumber = line.MedicineBatch.BatchNumber,
                line.Quantity,
                line.RefundAmount,
                line.Disposition,
                line.SalesReturn.ReturnedBy,
                line.SalesReturn.Reason,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = lines
            .Select(line => (IReadOnlyList<object?>)new object?[]
            {
                line.BusinessDate,
                line.ReturnNumber,
                line.InvoiceNumber,
                line.MedicineName,
                line.BatchNumber,
                line.Quantity,
                line.RefundAmount,
                line.Disposition,

                // Whoever took the return is named on the row, because a refund is money leaving the till.
                line.ReturnedBy,
                line.Reason,
            })
            .ToList();

        AddTotals(rows, "Total", 5, 6);

        return new ReportTable(
            ReportDefinitions.For(request.Kind).Title,
            request.Describe(),
            SalesReturnColumns,
            rows);
    }

    /// <summary>Appends a labelled totals row, summing only the columns that hold a number.</summary>
    /// <remarks>
    /// A column of counts stays a count and a column of money stays money, so the totals row reads like the
    /// rows above it. Columns holding dates, names or notes are left blank rather than given a meaningless
    /// sum.
    /// </remarks>
    private static void AddTotals(
        List<IReadOnlyList<object?>> rows,
        string label,
        params int[] numericColumns)
    {
        if (rows.Count == 0)
        {
            return;
        }

        var totals = new object?[rows[0].Count];
        totals[0] = label;

        foreach (var column in numericColumns)
        {
            var sum = 0m;
            var countsOnly = true;

            foreach (var row in rows)
            {
                switch (row[column])
                {
                    case decimal amount:
                        sum += amount;
                        countsOnly = false;
                        break;
                    case int count:
                        sum += count;
                        break;
                    case long count:
                        sum += count;
                        break;
                }
            }

            totals[column] = countsOnly && sum is >= int.MinValue and <= int.MaxValue
                ? (object)(int)sum
                : Money.Round(sum);
        }

        rows.Add(totals);
    }

    /// <summary>The invoice lines of the sales that count towards takings in a range.</summary>
    private static IQueryable<SaleLine> Range(IQueryable<SaleLine> source, DateOnly from, DateOnly to)
        => source.Where(line => line.Sale!.BusinessDate >= from
            && line.Sale.BusinessDate <= to
            && line.Sale.Status != SaleStatus.Cancelled);

    /// <summary>The sales that count towards takings in a range. Cancelled invoices are kept for audit.</summary>
    private static IQueryable<Sale> Range(IQueryable<Sale> source, DateOnly from, DateOnly to)
        => source.Where(sale => sale.BusinessDate >= from
            && sale.BusinessDate <= to
            && sale.Status != SaleStatus.Cancelled);
}