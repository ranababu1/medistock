namespace MediStock.Domain.Purchasing;

/// <summary>
/// Everything needed to turn one received purchase line into a batch. Returned by
/// <see cref="Purchase.Receive"/> so the caller can create the batches and their receipt movements
/// without re-deriving any values.
/// </summary>
/// <param name="LineId">Identifies the line the batch must be linked back to.</param>
public readonly record struct PurchaseLineReceipt(
    int LineId,
    int MedicineId,
    string BatchNumber,
    DateOnly ExpiryDate,
    decimal Quantity,
    decimal UnitCost,
    decimal SellingPrice);
