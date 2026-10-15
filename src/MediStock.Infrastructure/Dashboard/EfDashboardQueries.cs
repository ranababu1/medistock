using MediStock.Application.Common;
using MediStock.Application.Configuration;
using MediStock.Application.Dashboard;
using MediStock.Domain.Common;
using MediStock.Domain.Inventory;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;

namespace MediStock.Infrastructure.Dashboard;

/// <summary>
/// Read-only dashboard queries. Stock totals are computed from the batch balances that movements
/// maintain; nothing here writes to the database.
/// </summary>
internal sealed class EfDashboardQueries : IDashboardQueries
{
    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IClock _clock;
    private readonly MediStockOptions _options;

    public EfDashboardQueries(
        IDbContextFactory<MediStockDbContext> factory,
        IClock clock,
        MediStockOptions options)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var businessDate = _clock.BusinessDate;

        var medicineCount = await db.Medicines.CountAsync(cancellationToken).ConfigureAwait(false);
        var activeMedicineCount = await db.Medicines
            .CountAsync(m => m.IsActive, cancellationToken)
            .ConfigureAwait(false);
        var supplierCount = await db.Suppliers
            .CountAsync(s => s.IsActive, cancellationToken)
            .ConfigureAwait(false);

        var activeBatchCount = await db.MedicineBatches
            .CountAsync(
                b => b.Status == BatchStatus.Active && b.QuantityOnHand > 0 && b.ExpiryDate >= businessDate,
                cancellationToken)
            .ConfigureAwait(false);

        var expiredBatchCount = await db.MedicineBatches
            .CountAsync(
                b => b.QuantityOnHand > 0 && b.ExpiryDate < businessDate,
                cancellationToken)
            .ConfigureAwait(false);

        // Inventory valuation at cost, split into stock that may be dispensed today and stock that may
        // not. The costing method is documented in docs/decisions/0003-inventory-valuation.md.
        var inventoryRows = await db.MedicineBatches
            .AsNoTracking()
            .Where(b => b.QuantityOnHand > 0)
            .Select(b => new { b.Status, b.ExpiryDate, b.QuantityOnHand, b.PurchaseCost })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // Expiry eligibility is decided by ExpiryPolicy alone, so it is evaluated in memory rather
        // than restated in SQL.
        var sellableValue = 0m;
        var nonSellableValue = 0m;

        foreach (var row in inventoryRows)
        {
            var value = Money.Round(row.QuantityOnHand * row.PurchaseCost);

            if (ExpiryPolicy.IsSellable(row.Status, row.ExpiryDate, row.QuantityOnHand, businessDate))
            {
                sellableValue += value;
            }
            else
            {
                nonSellableValue += value;
            }
        }

        var lowStock = await GetLowStockAsync(500, cancellationToken).ConfigureAwait(false);
        var expiring = await GetExpiringBatchesAsync(
                withinDays: _options.NearExpiryDays,
                includeExpired: false,
                take: 500,
                cancellationToken)
            .ConfigureAwait(false);

        return new DashboardSnapshot(
            businessDate,
            medicineCount,
            activeMedicineCount,
            supplierCount,
            activeBatchCount,
            lowStock.Count,
            expiring.Count,
            expiredBatchCount,
            sellableValue + nonSellableValue,
            sellableValue,
            nonSellableValue);
    }

    public async Task<IReadOnlyList<LowStockItem>> GetLowStockAsync(
        int take = 25,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var businessDate = _clock.BusinessDate;

        var thresholds = await db.Medicines
            .AsNoTracking()
            .Where(m => m.IsActive && m.ReorderThreshold != null)
            .Select(m => new { m.Id, m.DisplayName, m.Strength, Threshold = m.ReorderThreshold!.Value })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (thresholds.Count == 0)
        {
            return [];
        }

        var balances = await db.MedicineBatches
            .AsNoTracking()
            .Where(b => b.Status == BatchStatus.Active && b.ExpiryDate >= businessDate && b.QuantityOnHand > 0)
            .GroupBy(b => b.MedicineId)
            .Select(g => new
            {
                MedicineId = g.Key,
                Quantity = g.Sum(b => b.QuantityOnHand),
                BatchCount = g.Count(),
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byMedicine = balances.ToDictionary(b => b.MedicineId);

        return thresholds
            .Select(m =>
            {
                byMedicine.TryGetValue(m.Id, out var balance);
                return new LowStockItem(
                    m.Id,
                    m.DisplayName,
                    m.Strength,
                    balance?.Quantity ?? 0m,
                    m.Threshold,
                    balance?.BatchCount ?? 0);
            })
            .Where(item => item.QuantityOnHand <= item.ReorderThreshold)
            .OrderByDescending(item => item.ReorderThreshold - item.QuantityOnHand)
            .ThenBy(item => item.DisplayName)
            .Take(Math.Max(take, 1))
            .ToList();
    }

    public async Task<IReadOnlyList<ExpiringBatch>> GetExpiringBatchesAsync(
        int withinDays,
        bool includeExpired = true,
        int take = 25,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var businessDate = _clock.BusinessDate;
        var limit = businessDate.AddDays(Math.Max(withinDays, 0));

        var query = db.MedicineBatches
            .AsNoTracking()
            .Where(b => b.QuantityOnHand > 0 && b.Status != BatchStatus.Depleted)
            .Where(b => b.ExpiryDate <= limit);

        if (!includeExpired)
        {
            query = query.Where(b => b.ExpiryDate >= businessDate);
        }

        var rows = await query
            .OrderBy(b => b.ExpiryDate)
            .ThenBy(b => b.BatchNumber)
            .Take(Math.Max(take, 1))
            .Select(b => new
            {
                b.Id,
                b.MedicineId,
                MedicineName = b.Medicine.DisplayName,
                b.BatchNumber,
                b.ExpiryDate,
                b.QuantityOnHand,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        // DayNumber arithmetic is done in memory because it has no SQL translation.
        return rows
            .Select(b => new ExpiringBatch(
                b.Id,
                b.MedicineId,
                b.MedicineName,
                b.BatchNumber,
                b.ExpiryDate,
                b.QuantityOnHand,
                b.ExpiryDate.DayNumber - businessDate.DayNumber))
            .ToList();
    }
}
