namespace MediStock.Domain.Inventory;

/// <summary>
/// Read model used by <see cref="FefoAllocator"/>. Kept free of Entity Framework types so the
/// allocation rules can be unit tested without a database.
/// </summary>
public sealed record BatchAvailability(
    int BatchId,
    string BatchNumber,
    DateOnly ExpiryDate,
    decimal AvailableQuantity,
    BatchStatus Status = BatchStatus.Active);

/// <summary>A quantity taken from one batch to fulfil a requirement.</summary>
public sealed record BatchAllocation(int BatchId, string BatchNumber, DateOnly ExpiryDate, decimal Quantity);
