using MediStock.Application.Common;
using MediStock.Application.Configuration;
using MediStock.Application.Dashboard;
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
    private readonly MediStockDbContext _db;
    private readonly IClock _clock;
    private readonly MediStockOptions _options;

    public EfDashboardQueries(MediStockDbContext db, IClock clock, MediStockOptions options)
    {
        _db = db ?? throw new ArgumentNullException(nameof(db));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public async Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default)
    {
        var businessDate = _clock.BusinessDate;

        var medicineCount = await _db.Medicines.CountAsync(cancellationToken).ConfigureAwait(false);
        var activeMedicineCount = await _db.Medicines
            .CountAsync(m => m.IsActive, cancellationToken)
            .ConfigureAwait(false);
        var supplierCount = await _db.Suppliers
            .CountAsync(s => s.IsActive, cancellationToken)
            .ConfigureAwait(false);

        var activeBatchCount = await _db.MedicineBatches
            .CountAsync(
                b => b.Status == BatchStatus.Active && b.QuantityOnHand > 0 && b.ExpiryDate >= businessDate,
                cancellationToken)
            .ConfigureAwait(false);

        var expiredBatchCount = await _db.MedicineBatches
            .CountAsync(
                b => b.QuantityOnHand > 0 && b.ExpiryDate < businessDate,
                cancellationToken)
            .ConfigureAwait(false);

        // Inventory valuation at cost. The costing method is documented in
        // docs/decisions/0003-inventory-valuation.md.
        var inventoryValue = await _db.MedicineBatches
            .Where(b => b.QuantityOnHand > 0)
            .SumAsync(b => b.QuantityOnHand * b.PurchaseCost, cancellationToken)
            .ConfigureAwait(false);

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
            inventoryValue);
    }

    public async Task<IReadOnlyList<LowStockItem>> GetLowStockAsync(
        int take = 25,
        CancellationToken cancellationToken = default)
    {
        var businessDate = _clock.BusinessDate;

        var thresholds = await _db.Medicines
            .AsNoTracking()
            .Where(m => m.IsActive && m.ReorderThreshold != null)
            .Select(m => new { m.Id, m.DisplayName, m.Strength, Threshold = m.ReorderThreshold!.Value })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        if (thresholds.Count == 0)
        {
            return [];
        }

        var balances = await _db.MedicineBatches
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
        var businessDate = _clock.BusinessDate;
        var limit = businessDate.AddDays(Math.Max(withinDays, 0));

        var query = _db.MedicineBatches
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
