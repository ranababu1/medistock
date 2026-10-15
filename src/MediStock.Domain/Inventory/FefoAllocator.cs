namespace MediStock.Domain.Inventory;

/// <summary>
/// Default batch allocation policy: first-expiry, first-out.
/// </summary>
/// <remarks>
/// Batches are consumed in ascending expiry date, with the batch id as a stable tie-breaker so that
/// two runs over the same data always produce the same allocation. Only batches that are active,
/// in stock, and not expired on the business date are considered. The allocator never partially
/// fulfils a request: if the eligible total is short it throws instead of returning a short pick,
/// which is what keeps the point-of-sale transaction from committing a half-sale.
/// </remarks>
public static class FefoAllocator
{
    public static IReadOnlyList<BatchAllocation> Allocate(
        IEnumerable<BatchAvailability> batches,
        decimal requiredQuantity,
        DateOnly businessDate)
    {
        ArgumentNullException.ThrowIfNull(batches);

        if (requiredQuantity <= 0m)
        {
            throw new ArgumentOutOfRangeException(
                nameof(requiredQuantity), requiredQuantity, "Required quantity must be greater than zero.");
        }

        var eligible = batches
            .Where(b => ExpiryPolicy.IsSellable(b.Status, b.ExpiryDate, b.AvailableQuantity, businessDate))
            .OrderBy(b => b.ExpiryDate)
            .ThenBy(b => b.BatchId)
            .ToList();

        var available = eligible.Sum(b => b.AvailableQuantity);
        if (available < requiredQuantity)
        {
            throw new InsufficientStockException(requiredQuantity, available);
        }

        var remaining = requiredQuantity;
        var allocations = new List<BatchAllocation>();

        foreach (var batch in eligible)
        {
            if (remaining <= 0m)
            {
                break;
            }

            var quantity = Math.Min(batch.AvailableQuantity, remaining);
            allocations.Add(new BatchAllocation(batch.BatchId, batch.BatchNumber, batch.ExpiryDate, quantity));
            remaining -= quantity;
        }

        return allocations;
    }

    /// <summary>Projects batches into the read model the allocator expects.</summary>
    public static IReadOnlyList<BatchAvailability> ToAvailability(IEnumerable<MedicineBatch> batches)
    {
        ArgumentNullException.ThrowIfNull(batches);

        return batches
            .Select(b => new BatchAvailability(b.Id, b.BatchNumber, b.ExpiryDate, b.QuantityOnHand, b.Status))
            .ToList();
    }
}
