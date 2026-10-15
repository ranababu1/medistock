namespace MediStock.Application.Dashboard;

/// <summary>Headline figures for the dashboard. Every value is traceable to a query in
/// <see cref="IDashboardQueries"/> and to the business date it was calculated for.</summary>
public sealed record DashboardSnapshot(
    DateOnly BusinessDate,
    int MedicineCount,
    int ActiveMedicineCount,
    int SupplierCount,
    int ActiveBatchCount,
    int LowStockCount,
    int NearExpiryBatchCount,
    int ExpiredBatchCount,
    decimal InventoryValueAtCost,
    decimal SellableInventoryValueAtCost = 0m,
    decimal NonSellableInventoryValueAtCost = 0m)
{
    public static DashboardSnapshot Empty(DateOnly businessDate)
        => new(businessDate, 0, 0, 0, 0, 0, 0, 0, 0m, 0m, 0m);
}

/// <summary>A medicine whose total sellable quantity is at or below its reorder threshold.</summary>
public sealed record LowStockItem(
    int MedicineId,
    string DisplayName,
    string? Strength,
    decimal QuantityOnHand,
    decimal ReorderThreshold,
    int BatchCount);

/// <summary>A batch approaching, or already past, its expiry date.</summary>
public sealed record ExpiringBatch(
    int BatchId,
    int MedicineId,
    string MedicineName,
    string BatchNumber,
    DateOnly ExpiryDate,
    decimal QuantityOnHand,
    int DaysUntilExpiry);

/// <summary>Read-only queries behind the dashboard and the inventory alerts.</summary>
public interface IDashboardQueries
{
    Task<DashboardSnapshot> GetSnapshotAsync(CancellationToken cancellationToken = default);

    Task<IReadOnlyList<LowStockItem>> GetLowStockAsync(
        int take = 25,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ExpiringBatch>> GetExpiringBatchesAsync(
        int withinDays,
        bool includeExpired = true,
        int take = 25,
        CancellationToken cancellationToken = default);
}
