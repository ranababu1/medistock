using MediStock.Application.Common;
using MediStock.Application.Inventory;
using MediStock.Domain.Common;
using MediStock.Domain.Inventory;
using MediStock.Domain.Sales;
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

    public async Task<IReadOnlyList<QuarantinedStockDto>> ListQuarantinedAsync(
        QuarantinedStockQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        // Source one: quantities a customer brought back that the pharmacist kept back rather than
        // restocking or destroying. They are not on any batch's balance any more - the batch gave them up
        // when they were sold - so the return lines are the only record of where they are.
        var customerReturns = await db.SalesReturnLines
            .AsNoTracking()
            .Where(line => line.Disposition == ReturnDisposition.Quarantine)
            .OrderByDescending(line => line.SalesReturn.BusinessDate)
            .ThenByDescending(line => line.Id)
            .Take(QuarantinedStockQuery.MaxResults)
            .Select(line => new
            {
                line.MedicineBatchId,
                line.MedicineBatch.MedicineId,
                MedicineName = line.MedicineBatch.Medicine.DisplayName,
                line.MedicineBatch.Medicine.Strength,
                line.MedicineBatch.BatchNumber,
                line.MedicineBatch.ExpiryDate,
                line.Quantity,
                Reason = line.SalesReturn.Reason,
                QuarantinedOn = line.SalesReturn.BusinessDate,
                Reference = line.SalesReturn.ReturnNumber,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Sources two and three: batches blocked by a write-off or a recall that still hold quantity to
        // deal with. The quantity is still on their balance, but it can no longer be sold, restocked or
        // sent back to the supplier, so it is quarantine in every sense that matters to the pharmacist.
        var batches = await db.MedicineBatches
            .AsNoTracking()
            .Where(batch => batch.QuantityOnHand > 0
                && (batch.Status == BatchStatus.Recalled || batch.Status == BatchStatus.WrittenOff))
            .OrderByDescending(batch => batch.UpdatedAtUtc)
            .Take(QuarantinedStockQuery.MaxResults)
            .Select(batch => new
            {
                batch.Id,
                batch.MedicineId,
                MedicineName = batch.Medicine.DisplayName,
                batch.Medicine.Strength,
                batch.BatchNumber,
                batch.ExpiryDate,
                Quantity = batch.QuantityOnHand,
                batch.Status,
                Reason = batch.StatusReason,
                batch.UpdatedAtUtc,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var rows = new List<QuarantinedStockDto>();

        rows.AddRange(customerReturns.Select(line => new QuarantinedStockDto
        {
            Source = QuarantineSource.CustomerReturn,
            MedicineId = line.MedicineId,
            MedicineName = line.MedicineName,
            Strength = line.Strength,
            MedicineBatchId = line.MedicineBatchId,
            BatchNumber = line.BatchNumber,
            ExpiryDate = line.ExpiryDate,
            Quantity = line.Quantity,
            Reason = line.Reason ?? string.Empty,
            QuarantinedOn = line.QuarantinedOn,
            Reference = line.Reference ?? string.Empty,
        }));

        rows.AddRange(batches.Select(batch => new QuarantinedStockDto
        {
            Source = batch.Status == BatchStatus.Recalled ? QuarantineSource.Recall : QuarantineSource.WriteOff,
            MedicineId = batch.MedicineId,
            MedicineName = batch.MedicineName,
            Strength = batch.Strength,
            MedicineBatchId = batch.Id,
            BatchNumber = batch.BatchNumber,
            ExpiryDate = batch.ExpiryDate,
            Quantity = batch.Quantity,
            Reason = batch.Reason ?? string.Empty,
            // The business date the block was last recorded, which is the date the stock entered this
            // state. A batch shows the day its status changed, a return line the day it was taken back.
            QuarantinedOn = DateOnly.FromDateTime(batch.UpdatedAtUtc.LocalDateTime),
            Reference = batch.BatchNumber,
        }));

        IEnumerable<QuarantinedStockDto> filtered = rows;

        if (Normalise(query.Term) is { } term)
        {
            filtered = filtered.Where(row =>
                row.MedicineName.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.BatchNumber.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Reason.Contains(term, StringComparison.OrdinalIgnoreCase));
        }

        var take = Math.Clamp(query.Take, 1, QuarantinedStockQuery.MaxResults);

        return
        [
            .. filtered
                .OrderByDescending(row => row.QuarantinedOn)
                .ThenBy(row => row.Source)
                .ThenBy(row => row.MedicineName, StringComparer.Ordinal)
                .ThenBy(row => row.MedicineBatchId)
                .Take(take),
        ];
    }
    private static string? Normalise(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string term)
        => term.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);
}
