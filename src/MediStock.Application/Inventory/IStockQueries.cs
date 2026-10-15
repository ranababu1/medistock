using MediStock.Domain.Inventory;

namespace MediStock.Application.Inventory;

/// <summary>One batch as shown on the inventory screen.</summary>
public sealed record BatchStockDto
{
    public int BatchId { get; init; }

    public int MedicineId { get; init; }

    public string MedicineName { get; init; } = string.Empty;

    public string? Strength { get; init; }

    public string BatchNumber { get; init; } = string.Empty;

    public DateOnly ExpiryDate { get; init; }

    public DateOnly ReceivedOn { get; init; }

    public decimal QuantityReceived { get; init; }

    public decimal QuantityOnHand { get; init; }

    public decimal PurchaseCost { get; init; }

    public decimal SellingPrice { get; init; }

    public BatchStatus Status { get; init; } = BatchStatus.Active;

    public string? StatusReason { get; init; }

    public int? SupplierId { get; init; }

    public string? SupplierName { get; init; }

    public string? SupplierInvoiceNumber { get; init; }

    /// <summary>
    /// Days from the business date to <see cref="ExpiryDate"/>. Negative once expired. Computed
    /// against the business date so reports and screens agree.
    /// </summary>
    public int DaysUntilExpiry { get; init; }

    /// <summary>Sellable today, from <see cref="ExpiryPolicy"/> — the single owner of that decision.</summary>
    public bool IsSellable { get; init; }

    /// <summary>Stock still on the shelf at cost, whether or not it may be dispensed.</summary>
    public decimal ValueAtCost { get; init; }
}

/// <summary>Filter for the inventory screen.</summary>
public sealed record BatchStockQuery
{
    public const int MaxResults = 1000;

    /// <summary>Free text matched against medicine name, generic name and batch number.</summary>
    public string? Term { get; init; }

    public int? MedicineId { get; init; }

    public int? SupplierId { get; init; }

    public BatchStatus? Status { get; init; }

    public DateOnly? ExpiringOnOrBefore { get; init; }

    /// <summary>When true, only rows with stock on hand are returned.</summary>
    public bool InStockOnly { get; init; } = true;

    public int Take { get; init; } = 300;
}

/// <summary>One immutable ledger entry, as shown in the stock movement history.</summary>
public sealed record StockMovementDto
{
    public int Id { get; init; }

    public int MedicineId { get; init; }

    public string MedicineName { get; init; } = string.Empty;

    public int MedicineBatchId { get; init; }

    public string BatchNumber { get; init; } = string.Empty;

    public StockMovementType MovementType { get; init; }

    public decimal QuantityDelta { get; init; }

    public decimal QuantityAfter { get; init; }

    public DateOnly BusinessDate { get; init; }

    public DateTimeOffset OccurredAtUtc { get; init; }

    public string PerformedBy { get; init; } = string.Empty;

    public string? Reason { get; init; }

    public string? Reference { get; init; }
}

/// <summary>Filter for the stock movement history.</summary>
public sealed record StockMovementQuery
{
    public const int MaxResults = 1000;

    public int? MedicineId { get; init; }

    public int? MedicineBatchId { get; init; }

    public DateOnly? FromBusinessDate { get; init; }

    public DateOnly? ToBusinessDate { get; init; }

    public StockMovementType? MovementType { get; init; }

    public int Take { get; init; } = 200;
}

/// <summary>Result of comparing cached batch balances against the movement ledger.</summary>
public sealed record ReconciliationSummary(
    int BatchesChecked,
    int BatchesMismatched,
    decimal LedgerQuantity,
    decimal CachedQuantity)
{
    public bool IsReconciled => BatchesMismatched == 0;
}

/// <summary>Read-only inventory queries behind the inventory and stock-history screens.</summary>
public interface IStockQueries
{
    Task<IReadOnlyList<BatchStockDto>> ListBatchesAsync(
        BatchStockQuery query,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<StockMovementDto>> ListMovementsAsync(
        StockMovementQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Compares every cached batch balance with the sum of its movements. The ledger is the source of
    /// truth, so any mismatch is a defect worth surfacing rather than hiding.
    /// </summary>
    Task<ReconciliationSummary> CheckReconciliationAsync(CancellationToken cancellationToken = default);
}
