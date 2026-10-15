using MediStock.Domain.Common;

namespace MediStock.Domain.Inventory;

/// <summary>Kind of movement applied to a batch. Values are persisted and must stay stable.</summary>
public enum StockMovementType
{
    /// <summary>Goods received from a supplier.</summary>
    Receipt = 1,

    /// <summary>Sold to a customer.</summary>
    Sale = 2,

    /// <summary>Customer returned goods that have been accepted back into the batch.</summary>
    SalesReturn = 3,

    /// <summary>Positive correction, for example a stock-count gain.</summary>
    AdjustmentIncrease = 4,

    /// <summary>Negative correction, for example breakage or a stock-count loss.</summary>
    AdjustmentDecrease = 5,

    /// <summary>Expired stock removed from sellable inventory.</summary>
    ExpiryWriteOff = 6,

    /// <summary>Goods sent back to the supplier.</summary>
    SupplierReturn = 7,

    /// <summary>Stock blocked or removed because of a manufacturer recall.</summary>
    RecallWriteOff = 8,

    /// <summary>Auditable reversal of an earlier receipt.</summary>
    ReceiptReversal = 9,
}

/// <summary>
/// Append-only ledger entry for every change to batch stock. The ledger, not the cached
/// <see cref="MedicineBatch.QuantityOnHand"/>, is the source of truth for reconciliation.
/// </summary>
public class StockMovement
{
    public const int ReasonMaxLength = 300;
    public const int ReferenceMaxLength = 100;
    public const int PerformedByMaxLength = 150;

    /// <summary>Required by Entity Framework Core.</summary>
    private StockMovement()
    {
    }

    public int Id { get; private set; }

    public int MedicineBatchId { get; private set; }

    public MedicineBatch MedicineBatch { get; private set; } = null!;

    /// <summary>Denormalised for stock reporting without joining through the batch table.</summary>
    public int MedicineId { get; private set; }

    public StockMovementType MovementType { get; private set; }

    /// <summary>Signed change. Positive increases stock, negative decreases it. Never zero.</summary>
    public decimal QuantityDelta { get; private set; }

    /// <summary>Batch balance immediately after this movement was applied.</summary>
    public decimal QuantityAfter { get; private set; }

    public string? Reason { get; private set; }

    /// <summary>Free-text link back to the document that caused the movement.</summary>
    public string? Reference { get; private set; }

    public string PerformedBy { get; private set; } = string.Empty;

    public DateTimeOffset OccurredAtUtc { get; private set; }

    /// <summary>Local business date used by daily reports, derived once so reports stay stable.</summary>
    public DateOnly BusinessDate { get; private set; }

    public static StockMovement Record(
        int medicineId,
        int medicineBatchId,
        StockMovementType movementType,
        decimal quantityDelta,
        decimal quantityAfter,
        DateOnly businessDate,
        string performedBy,
        string? reason = null,
        string? reference = null,
        DateTimeOffset? nowUtc = null)
    {
        if (!Enum.IsDefined(movementType))
        {
            throw new DomainValidationException($"'{movementType}' is not a supported stock movement type.");
        }

        if (quantityDelta == 0m)
        {
            throw new DomainValidationException("A stock movement must change the quantity.");
        }

        if (quantityAfter < 0m)
        {
            throw new DomainValidationException("A stock movement cannot leave a negative batch balance.");
        }

        if (medicineId <= 0 || medicineBatchId <= 0)
        {
            throw new DomainValidationException("A stock movement must reference an existing medicine and batch.");
        }

        var reasonText = Guard.Optional(reason, ReasonMaxLength, "Reason");
        if (RequiresReason(movementType) && reasonText is null)
        {
            throw new DomainValidationException($"A reason is required for a {Describe(movementType)} movement.");
        }

        return new StockMovement
        {
            MedicineId = medicineId,
            MedicineBatchId = medicineBatchId,
            MovementType = movementType,
            QuantityDelta = quantityDelta,
            QuantityAfter = quantityAfter,
            BusinessDate = businessDate,
            PerformedBy = Guard.Required(performedBy, PerformedByMaxLength, "Operator"),
            Reason = reasonText,
            Reference = Guard.Optional(reference, ReferenceMaxLength, "Reference"),
            OccurredAtUtc = nowUtc ?? DateTimeOffset.UtcNow,
        };
    }

    /// <summary>Manual corrections and write-offs are only acceptable when justified.</summary>
    public static bool RequiresReason(StockMovementType movementType) => movementType switch
    {
        StockMovementType.AdjustmentIncrease => true,
        StockMovementType.AdjustmentDecrease => true,
        StockMovementType.ExpiryWriteOff => true,
        StockMovementType.RecallWriteOff => true,
        StockMovementType.ReceiptReversal => true,
        _ => false,
    };

    private static string Describe(StockMovementType movementType) => movementType switch
    {
        StockMovementType.AdjustmentIncrease => "stock increase adjustment",
        StockMovementType.AdjustmentDecrease => "stock decrease adjustment",
        StockMovementType.ExpiryWriteOff => "expiry write-off",
        StockMovementType.RecallWriteOff => "recall write-off",
        StockMovementType.ReceiptReversal => "receipt reversal",
        _ => movementType.ToString(),
    };
}
