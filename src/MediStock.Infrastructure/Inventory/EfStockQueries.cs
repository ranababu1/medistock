using MediStock.Application.Common;
using MediStock.Application.Inventory;
using MediStock.Domain.Common;
using MediStock.Domain.Inventory;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MediStock.Infrastructure.Inventory;

/// <summary>
/// Entity Framework Core inventory queries behind the batch list, the stock movement history and the
/// reconciliation check.
/// </summary>
internal sealed class EfStockQueries : IStockQueries
{
    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IClock _clock;

    public EfStockQueries(IDbContextFactory<MediStockDbContext> factory, IClock clock)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public async Task<IReadOnlyList<BatchStockDto>> ListBatchesAsync(
        BatchStockQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var businessDate = _clock.BusinessDate;
        var source = db.MedicineBatches.AsNoTracking();

        if (query.InStockOnly)
        {
            source = source.Where(b => b.QuantityOnHand > 0);
        }

        if (query.MedicineId is { } medicineId)
        {
            source = source.Where(b => b.MedicineId == medicineId);
        }

        if (query.SupplierId is { } supplierId)
        {
            source = source.Where(b => b.SupplierId == supplierId);
        }

        if (query.Status is { } status)
        {
            source = source.Where(b => b.Status == status);
        }

        if (query.ExpiringOnOrBefore is { } limit)
        {
            source = source.Where(b => b.ExpiryDate <= limit);
        }

        if (Normalise(query.Term) is { } term)
        {
            var pattern = $"%{EscapeLike(term)}%";
            source = source.Where(b =>
                EF.Functions.Like(b.BatchNumber, pattern)
                || EF.Functions.Like(b.Medicine.DisplayName, pattern)
                || (b.Medicine.GenericName != null && EF.Functions.Like(b.Medicine.GenericName, pattern)));
        }

        var take = Math.Clamp(query.Take, 1, BatchStockQuery.MaxResults);

        var rows = await source
            .OrderBy(b => b.ExpiryDate)
            .ThenBy(b => b.MedicineId)
            .ThenBy(b => b.Id)
            .Take(take)
            .Select(b => new
            {
                b.Id,
                b.MedicineId,
                MedicineName = b.Medicine.DisplayName,
                b.Medicine.Strength,
                b.BatchNumber,
                b.ExpiryDate,
                b.ReceivedOn,
                b.QuantityReceived,
                b.QuantityOnHand,
                b.PurchaseCost,
                b.SellingPrice,
                b.Status,
                b.StatusReason,
                b.SupplierId,
                SupplierName = b.Supplier != null ? b.Supplier.Name : null,
                b.SupplierInvoiceNumber,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Expiry decisions and money rounding stay in memory so that the rule has exactly one owner.
        return
        [
            .. rows.Select(row => new BatchStockDto
            {
                BatchId = row.Id,
                MedicineId = row.MedicineId,
                MedicineName = row.MedicineName,
                Strength = row.Strength,
                BatchNumber = row.BatchNumber,
                ExpiryDate = row.ExpiryDate,
                ReceivedOn = row.ReceivedOn,
                QuantityReceived = row.QuantityReceived,
                QuantityOnHand = row.QuantityOnHand,
                PurchaseCost = row.PurchaseCost,
                SellingPrice = row.SellingPrice,
                Status = row.Status,
                StatusReason = row.StatusReason,
                SupplierId = row.SupplierId,
                SupplierName = row.SupplierName,
                SupplierInvoiceNumber = row.SupplierInvoiceNumber,
                DaysUntilExpiry = row.ExpiryDate.DayNumber - businessDate.DayNumber,
                IsSellable = ExpiryPolicy.IsSellable(row.Status, row.ExpiryDate, row.QuantityOnHand, businessDate),
                ValueAtCost = Money.Round(row.QuantityOnHand * row.PurchaseCost),
            }),
        ];
    }

    public async Task<IReadOnlyList<StockMovementDto>> ListMovementsAsync(
        StockMovementQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = db.StockMovements.AsNoTracking();

        if (query.MedicineId is { } medicineId)
        {
            source = source.Where(m => m.MedicineId == medicineId);
        }

        if (query.MedicineBatchId is { } batchId)
        {
            source = source.Where(m => m.MedicineBatchId == batchId);
        }

        if (query.MovementType is { } movementType)
        {
            source = source.Where(m => m.MovementType == movementType);
        }

        if (query.FromBusinessDate is { } from)
        {
            source = source.Where(m => m.BusinessDate >= from);
        }

        if (query.ToBusinessDate is { } to)
        {
            source = source.Where(m => m.BusinessDate <= to);
        }

        var take = Math.Clamp(query.Take, 1, StockMovementQuery.MaxResults);

        return await source
            .OrderByDescending(m => m.OccurredAtUtc)
            .ThenByDescending(m => m.Id)
            .Take(take)
            .Select(m => new StockMovementDto
            {
                Id = m.Id,
                MedicineId = m.MedicineId,
                MedicineName = m.MedicineBatch.Medicine.DisplayName,
                MedicineBatchId = m.MedicineBatchId,
                BatchNumber = m.MedicineBatch.BatchNumber,
                MovementType = m.MovementType,
                QuantityDelta = m.QuantityDelta,
                QuantityAfter = m.QuantityAfter,
                BusinessDate = m.BusinessDate,
                OccurredAtUtc = m.OccurredAtUtc,
                PerformedBy = m.PerformedBy,
                Reason = m.Reason,
                Reference = m.Reference,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<ReconciliationSummary> CheckReconciliationAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var cached = await db.MedicineBatches
            .AsNoTracking()
            .Select(b => new { b.Id, b.QuantityOnHand })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ledger = await db.StockMovements
            .AsNoTracking()
            .GroupBy(m => m.MedicineBatchId)
            .Select(group => new { BatchId = group.Key, Quantity = group.Sum(m => m.QuantityDelta) })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var ledgerByBatch = ledger.ToDictionary(row => row.BatchId, row => row.Quantity);

        var mismatched = 0;
        var ledgerQuantity = 0m;
        var cachedQuantity = 0m;

        foreach (var batch in cached)
        {
            ledgerByBatch.TryGetValue(batch.Id, out var movementTotal);

            ledgerQuantity += movementTotal;
            cachedQuantity += batch.QuantityOnHand;

            if (movementTotal != batch.QuantityOnHand)
            {
                mismatched++;
            }
        }

        return new ReconciliationSummary(cached.Count, mismatched, ledgerQuantity, cachedQuantity);
    }

    private static string? Normalise(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string term)
        => term.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);
}
