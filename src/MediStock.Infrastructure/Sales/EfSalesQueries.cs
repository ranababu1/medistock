using MediStock.Application.Sales;
using MediStock.Domain.Sales;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MediStock.Infrastructure.Sales;

/// <summary>
/// Entity Framework Core sales aggregates behind the dashboard and the reports.
/// </summary>
/// <remarks>
/// Every figure here is summed from the <c>Sales</c> and <c>SaleLines</c> rows rather than recomputed from
/// quantities and prices, so a report can never disagree with the invoice that produced it. Cancelled
/// sales are excluded throughout: they are kept for the audit trail, not counted as takings.
/// </remarks>
internal sealed class EfSalesQueries : ISalesQueries
{
    private readonly IDbContextFactory<MediStockDbContext> _factory;

    public EfSalesQueries(IDbContextFactory<MediStockDbContext> factory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<DailySalesSummary> GetDailySummaryAsync(
        DateOnly businessDate,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var header = await db.Sales
            .AsNoTracking()
            .Where(sale => sale.BusinessDate == businessDate && sale.Status != SaleStatus.Cancelled)
            .GroupBy(_ => 1)
            .Select(group => new
            {
                SaleCount = group.Count(),
                SubTotal = group.Sum(sale => sale.SubTotal),
                DiscountTotal = group.Sum(sale => sale.DiscountTotal),
                TaxTotal = group.Sum(sale => sale.TaxTotal),
                Total = group.Sum(sale => sale.Total),
                CostTotal = group.Sum(sale => sale.CostTotal),
            })
            // Single rather than First: grouping by a constant yields at most one row, and anything else
            // would mean the SQL no longer matches this method's contract.
            .SingleOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false);

        // Quantities live on the lines rather than the header, so they need their own aggregate.
        var units = await db.SaleLines
            .AsNoTracking()
            .Where(line => line.Sale!.BusinessDate == businessDate
                && line.Sale.Status != SaleStatus.Cancelled)
            .SumAsync(line => (decimal?)line.Quantity, cancellationToken)
            .ConfigureAwait(false);

        // A day can have returns without sales, so the returns are read even when the sales aggregate
        // found nothing — otherwise the till would report a refund that never happened.
        var returns = await LoadReturnsAsync(db, businessDate, businessDate, cancellationToken)
            .ConfigureAwait(false);
        var refunds = returns.GetValueOrDefault(businessDate);

        return new DailySalesSummary(
            businessDate,
            header?.SaleCount ?? 0,
            refunds?.ReturnCount ?? 0,
            header?.SubTotal ?? 0m,
            header?.DiscountTotal ?? 0m,
            header?.TaxTotal ?? 0m,
            header?.Total ?? 0m,
            header?.CostTotal ?? 0m,
            units ?? 0m,
            refunds?.UnitsReturned ?? 0m,
            refunds?.RefundTotal ?? 0m);
    }

    public async Task<IReadOnlyList<DailySalesSummary>> GetDailySummariesAsync(
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken = default)
    {
        EnsureOrdered(from, to);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Grouped in SQL, re-projected in memory: SqlServer cannot translate a record constructor inside
        // GroupBy, and one row per trading day is cheap to materialise. Quantities live on the lines, so
        // they take a second set-based pass; the day count does not multiply round trips.
        var headers = await db.Sales
            .AsNoTracking()
            .Where(sale => sale.BusinessDate >= from
                && sale.BusinessDate <= to
                && sale.Status != SaleStatus.Cancelled)
            .GroupBy(sale => sale.BusinessDate)
            .Select(group => new
            {
                BusinessDate = group.Key,
                SaleCount = group.Count(),
                SubTotal = group.Sum(sale => sale.SubTotal),
                DiscountTotal = group.Sum(sale => sale.DiscountTotal),
                TaxTotal = group.Sum(sale => sale.TaxTotal),
                Total = group.Sum(sale => sale.Total),
                CostTotal = group.Sum(sale => sale.CostTotal),
            })
            .OrderBy(row => row.BusinessDate)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (headers.Count == 0)
        {
            return [];
        }

        var quantities = await db.SaleLines
            .AsNoTracking()
            .Where(line => line.Sale!.BusinessDate >= from
                && line.Sale.BusinessDate <= to
                && line.Sale.Status != SaleStatus.Cancelled)
            .GroupBy(line => line.Sale!.BusinessDate)
            .Select(group => new { BusinessDate = group.Key, Units = group.Sum(line => line.Quantity) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var unitsByDate = quantities.ToDictionary(row => row.BusinessDate, row => row.Units);
        var returns = await LoadReturnsAsync(db, from, to, cancellationToken).ConfigureAwait(false);

        return [.. headers.Select(row => new DailySalesSummary(
            row.BusinessDate,
            row.SaleCount,
            returns.GetValueOrDefault(row.BusinessDate)?.ReturnCount ?? 0,
            row.SubTotal,
            row.DiscountTotal,
            row.TaxTotal,
            row.Total,
            row.CostTotal,
            unitsByDate.TryGetValue(row.BusinessDate, out var units) ? units : 0m,
            returns.GetValueOrDefault(row.BusinessDate)?.UnitsReturned ?? 0m,
            returns.GetValueOrDefault(row.BusinessDate)?.RefundTotal ?? 0m))];
    }

    public async Task<IReadOnlyList<PaymentMethodTotal>> GetPaymentMethodTotalsAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var taken = await Range(db.Sales, from, to)
            .SelectMany(sale => sale.Payments)
            .GroupBy(payment => payment.Method)
            .Select(group => new
            {
                Method = group.Key,
                SaleCount = group.Select(payment => payment.SaleId).Distinct().Count(),
                Amount = group.Sum(payment => payment.Amount),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Refunds are netted off the method the money came back through, because that is the method's
        // drawer or terminal the cashier has to reconcile. A method that only saw refunds still appears.
        var refunded = await db.SalesReturns
            .AsNoTracking()
            .Where(salesReturn => (from == null || salesReturn.BusinessDate >= from)
                && (to == null || salesReturn.BusinessDate <= to))
            .GroupBy(salesReturn => salesReturn.RefundMethod)
            .Select(group => new { Method = group.Key, Amount = group.Sum(row => row.RefundTotal) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var refundsByMethod = refunded.ToDictionary(row => row.Method, row => row.Amount);

        return
        [
            .. taken
                .Select(row => new PaymentMethodTotal(
                    row.Method,
                    row.SaleCount,
                    row.Amount,
                    refundsByMethod.GetValueOrDefault(row.Method)))
                .Concat(refunded
                    .Where(row => taken.All(takenRow => takenRow.Method != row.Method))
                    .Select(row => new PaymentMethodTotal(row.Method, 0, 0m, row.Amount)))
                .OrderBy(row => row.Method),
        ];
    }

    public async Task<IReadOnlyList<MedicineSalesTotal>> GetTopMedicinesAsync(
        DateOnly? from = null,
        DateOnly? to = null,
        int take = 25,
        CancellationToken cancellationToken = default)
    {
        var limit = Math.Clamp(take, 1, 200);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var rows = await Range(db.SaleLines, from, to)
            .GroupBy(line => new { line.MedicineId, line.MedicineName })
            .Select(group => new
            {
                group.Key.MedicineId,
                group.Key.MedicineName,
                UnitsSold = group.Sum(line => line.Quantity),
                GrossAmount = group.Sum(line => line.GrossAmount),
                DiscountAmount = group.Sum(line => line.DiscountAmount),
                TaxAmount = group.Sum(line => line.TaxAmount),
                Total = group.Sum(line => line.LineTotal),
                CostTotal = group.Sum(line => line.Quantity * line.UnitCost),
            })
            .OrderByDescending(row => row.Total)
            .ThenBy(row => row.MedicineName)
            .Take(limit)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. rows.Select(row => new MedicineSalesTotal(
            row.MedicineId,
            row.MedicineName,
            row.UnitsSold,
            row.GrossAmount,
            row.DiscountAmount,
            row.TaxAmount,
            row.Total,
            row.CostTotal))];
    }

    private static void EnsureOrdered(DateOnly? from, DateOnly? to)
    {
        if (to < from)
        {
            throw new ArgumentException($"The end of the range ({to}) precedes its start ({from}).", nameof(to));
        }
    }

    /// <summary>Applies the optional business-date range shared by the reporting queries.</summary>
    private static IQueryable<Sale> Range(IQueryable<Sale> source, DateOnly? from, DateOnly? to)
    {
        EnsureOrdered(from, to);

        source = source.Where(sale => sale.Status != SaleStatus.Cancelled);

        if (from is { } start)
        {
            source = source.Where(sale => sale.BusinessDate >= start);
        }

        if (to is { } end)
        {
            source = source.Where(sale => sale.BusinessDate <= end);
        }

        return source;
    }

    /// <summary>Applies the same range to sale lines through their parent sale.</summary>
    private static IQueryable<SaleLine> Range(IQueryable<SaleLine> source, DateOnly? from, DateOnly? to)
    {
        EnsureOrdered(from, to);

        source = source.Where(line => line.Sale!.Status != SaleStatus.Cancelled);

        if (from is { } start)
        {
            source = source.Where(line => line.Sale!.BusinessDate >= start);
        }

        if (to is { } end)
        {
            source = source.Where(line => line.Sale!.BusinessDate <= end);
        }

        return source;
    }

    /// <summary>
    /// Reads the returns recorded between two business dates, keyed by the date the refund counts
    /// towards. Returns are dated in their own right — a pack can come back days after the invoice —
    /// so they are never attached to the sale's date.
    /// </summary>
    private static async Task<Dictionary<DateOnly, ReturnTotals>> LoadReturnsAsync(
        MediStockDbContext db,
        DateOnly from,
        DateOnly to,
        CancellationToken cancellationToken)
    {
        var headers = await db.SalesReturns
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

        var quantities = await db.SalesReturnLines
            .AsNoTracking()
            .Where(line => line.SalesReturn.BusinessDate >= from && line.SalesReturn.BusinessDate <= to)
            .GroupBy(line => line.SalesReturn.BusinessDate)
            .Select(group => new { BusinessDate = group.Key, Units = group.Sum(line => line.Quantity) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var unitsByDate = quantities.ToDictionary(row => row.BusinessDate, row => row.Units);

        return headers.ToDictionary(
            row => row.BusinessDate,
            row => new ReturnTotals(
                row.ReturnCount,
                unitsByDate.TryGetValue(row.BusinessDate, out var units) ? units : 0m,
                row.RefundTotal));
    }

    /// <summary>What came back on one business date.</summary>
    private sealed record ReturnTotals(int ReturnCount, decimal UnitsReturned, decimal RefundTotal);
}
