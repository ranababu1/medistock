using MediStock.Domain.Common;
using MediStock.Domain.Inventory;

namespace MediStock.Domain.Sales;

/// <summary>
/// Records how much of one invoice line came out of one batch. This is the join that makes a sale
/// traceable back to the stock it consumed, and it is what a future return is credited against.
/// </summary>
/// <remarks>
/// The batch number and expiry date are copied at the time of sale, so reprinting an invoice years
/// later shows what the label actually said rather than what the batch row says today.
/// </remarks>
public class SaleLineBatchAllocation
{
    /// <summary>Required by Entity Framework Core.</summary>
    private SaleLineBatchAllocation()
    {
    }

    public int Id { get; private set; }

    public int SaleLineId { get; private set; }

    public SaleLine SaleLine { get; private set; } = null!;

    public int MedicineBatchId { get; private set; }

    public MedicineBatch MedicineBatch { get; private set; } = null!;

    /// <summary>Batch number as printed on the pack at the time of sale.</summary>
    public string BatchNumber { get; private set; } = string.Empty;

    /// <summary>Expiry date of that batch at the time of sale.</summary>
    public DateOnly ExpiryDate { get; private set; }

    /// <summary>Quantity taken from this batch. Always positive: a sale only ever removes stock.</summary>
    public decimal Quantity { get; private set; }

    internal static SaleLineBatchAllocation From(BatchAllocation allocation)
    {
        ArgumentNullException.ThrowIfNull(allocation);

        if (allocation.BatchId <= 0)
        {
            throw new DomainValidationException("An allocation must reference an existing batch.");
        }

        return new SaleLineBatchAllocation
        {
            MedicineBatchId = allocation.BatchId,
            BatchNumber = Guard.Required(
                allocation.BatchNumber, MedicineBatch.BatchNumberMaxLength, "Batch number"),
            ExpiryDate = allocation.ExpiryDate,
            Quantity = Guard.Positive(allocation.Quantity, "Allocated quantity"),
        };
    }
}
