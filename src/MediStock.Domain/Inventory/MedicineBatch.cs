using MediStock.Domain.Common;

namespace MediStock.Domain.Inventory;

/// <summary>
/// A single received batch of one medicine. Batch is the unit of stock because batch number and
/// expiry date drive what may legally and safely be dispensed.
/// </summary>
/// <remarks>
/// <see cref="QuantityOnHand"/> is a cached balance kept for query performance. Every change to it
/// must be accompanied by a <see cref="StockMovement"/> row; <c>InventoryReconciler</c> compares the
/// two.
/// </remarks>
public class MedicineBatch
{
    public const int BatchNumberMaxLength = 64;
    public const int InvoiceNumberMaxLength = 64;

    /// <summary>Required by Entity Framework Core.</summary>
    private MedicineBatch()
    {
    }

    public int Id { get; private set; }

    public int MedicineId { get; private set; }

    public Catalogue.Medicine Medicine { get; private set; } = null!;

    public string BatchNumber { get; private set; } = string.Empty;

    /// <summary>Last day the batch may be dispensed. See <see cref="ExpiryPolicy"/>.</summary>
    public DateOnly ExpiryDate { get; private set; }

    public decimal QuantityReceived { get; private set; }

    /// <summary>Cached balance. Reconcilable against <see cref="StockMovement"/> history.</summary>
    public decimal QuantityOnHand { get; private set; }

    /// <summary>Cost per unit paid to the supplier.</summary>
    public decimal PurchaseCost { get; private set; }

    /// <summary>Default selling price per unit for this batch.</summary>
    public decimal SellingPrice { get; private set; }

    public int? SupplierId { get; private set; }

    public Suppliers.Supplier? Supplier { get; private set; }

    public string? SupplierInvoiceNumber { get; private set; }

    /// <summary>Local business date the goods were received into stock.</summary>
    public DateOnly ReceivedOn { get; private set; }

    public DateTimeOffset ReceivedAtUtc { get; private set; }

    public DateTimeOffset UpdatedAtUtc { get; private set; }

    public BatchStatus Status { get; private set; } = BatchStatus.Active;

    /// <summary>Populated when the batch is written off or recalled.</summary>
    public string? StatusReason { get; private set; }

    public bool IsSellableOn(DateOnly businessDate)
        => ExpiryPolicy.IsSellable(Status, ExpiryDate, QuantityOnHand, businessDate);

    /// <summary>
    /// True while this batch can still be taken back out of stock by reversing its receipt: nothing
    /// has been sold or adjusted out of it, and it has not been recalled or written off.
    /// </summary>
    /// <remarks>
    /// A batch that later expired is still reversible — only its status changed, and the stock is
    /// physically still there, so reversing a mis-keyed expiry date is possible.
    /// </remarks>
    public bool IsReversibleReceipt =>
        QuantityOnHand == QuantityReceived && Status is not (BatchStatus.Recalled or BatchStatus.WrittenOff);

    /// <summary>
    /// True while this batch could have returned stock put back on its shelf. Expiry is judged against
    /// the business date of the return, not today, so a back-dated return is judged as of its own day.
    /// </summary>
    public bool CanBeRestocked(DateOnly businessDate) => RestockBlocker(businessDate) is null;

    /// <summary>Why this batch cannot take returned stock back, or <c>null</c> when it can.</summary>
    public string? RestockBlocker(DateOnly businessDate)
    {
        if (ExpiryPolicy.IsExpired(ExpiryDate, businessDate))
        {
            return $"it expired on {ExpiryDate:yyyy-MM-dd}";
        }

        return Status switch
        {
            BatchStatus.Active or BatchStatus.Depleted => null,
            BatchStatus.Expired => $"it expired on {ExpiryDate:yyyy-MM-dd}",
            BatchStatus.WrittenOff => "it has been written off",
            BatchStatus.Recalled => "it has been recalled",
            _ => $"it is {Status}",
        };
    }

    public static MedicineBatch Receive(
        int medicineId,
        string batchNumber,
        DateOnly expiryDate,
        decimal quantity,
        decimal purchaseCost,
        decimal sellingPrice,
        DateOnly receivedOn,
        int? supplierId = null,
        string? supplierInvoiceNumber = null,
        DateTimeOffset? nowUtc = null)
    {
        if (medicineId <= 0)
        {
            throw new DomainValidationException("A batch must reference an existing medicine.");
        }

        var quantityReceived = Guard.Positive(quantity, "Quantity received");
        if (expiryDate <= receivedOn)
        {
            throw new DomainValidationException(
                $"Batch '{batchNumber}' expires on {expiryDate:yyyy-MM-dd}, which is not after the receipt date {receivedOn:yyyy-MM-dd}. Already-expired stock cannot be received.");
        }

        var timestamp = nowUtc ?? DateTimeOffset.UtcNow;

        return new MedicineBatch
        {
            MedicineId = medicineId,
            BatchNumber = Guard.Required(batchNumber, BatchNumberMaxLength, "Batch number"),
            ExpiryDate = expiryDate,
            QuantityReceived = quantityReceived,
            QuantityOnHand = quantityReceived,
            PurchaseCost = Guard.NonNegative(Money.Round(purchaseCost), "Purchase cost"),
            SellingPrice = Guard.NonNegative(Money.Round(sellingPrice), "Selling price"),
            SupplierId = supplierId,
            SupplierInvoiceNumber = Guard.Optional(supplierInvoiceNumber, InvoiceNumberMaxLength, "Supplier invoice number"),
            ReceivedOn = receivedOn,
            ReceivedAtUtc = timestamp,
            UpdatedAtUtc = timestamp,
            Status = BatchStatus.Active,
        };
    }

    /// <summary>Applies an inward movement. Used for receipts, returns re-stocked, and gains.</summary>
    public void IncreaseQuantity(decimal quantity, DateTimeOffset? nowUtc = null)
    {
        Guard.Positive(quantity, "Quantity");
        EnsureMovable(BatchStatus.Recalled, "recalled");
        EnsureMovable(BatchStatus.WrittenOff, "written off");

        QuantityOnHand += quantity;
        QuantityReceived += quantity;
        if (Status == BatchStatus.Depleted)
        {
            Status = BatchStatus.Active;
        }

        UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Applies an outward movement. Throws rather than allowing a negative balance, so an
    /// accidental double-sale cannot silently corrupt inventory.
    /// </summary>
    public void DecreaseQuantity(decimal quantity, DateTimeOffset? nowUtc = null)
    {
        Guard.Positive(quantity, "Quantity");
        EnsureMovable(BatchStatus.Recalled, "recalled");
        EnsureMovable(BatchStatus.WrittenOff, "written off");

        if (quantity > QuantityOnHand)
        {
            throw new InsufficientStockException(BatchNumber, quantity, QuantityOnHand);
        }

        QuantityOnHand -= quantity;
        if (QuantityOnHand == 0m)
        {
            Status = BatchStatus.Depleted;
        }

        UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Puts returned stock back on the shelf.
    /// </summary>
    /// <remarks>
    /// Deliberately not an <see cref="IncreaseQuantity"/>: that records goods bought in, and bumps
    /// QuantityReceived, which describes what a supplier sent. Returned stock was never bought again,
    /// so only the cached balance moves and the batch stops being a reversible receipt — the goods it
    /// now holds are not the ones that arrived. A return that is quarantined or destroyed never reaches
    /// this method, so the cache stays equal to the sum of stock movements.
    /// </remarks>
    public void RestoreQuantity(decimal quantity, DateOnly businessDate, DateTimeOffset? nowUtc = null)
    {
        Guard.Positive(quantity, "Quantity");

        var blocker = RestockBlocker(businessDate);
        if (blocker is not null)
        {
            throw new DomainValidationException(
                $"Batch '{BatchNumber}' cannot take returned stock back: {blocker}.");
        }

        QuantityOnHand += quantity;
        if (Status == BatchStatus.Depleted)
        {
            Status = BatchStatus.Active;
        }

        UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
    }

    /// <summary>Removes the remaining balance from sellable stock with a recorded justification.</summary>
    public void WriteOff(string reason, BatchStatus status = BatchStatus.WrittenOff, DateTimeOffset? nowUtc = null)
    {
        if (status is not (BatchStatus.WrittenOff or BatchStatus.Expired or BatchStatus.Recalled))
        {
            throw new DomainValidationException($"'{status}' is not a valid write-off status.");
        }

        if (QuantityOnHand == 0m)
        {
            throw new DomainValidationException($"Batch '{BatchNumber}' has no quantity left to write off.");
        }

        StatusReason = Guard.Required(reason, StockMovement.ReasonMaxLength, "Reason");
        Status = status;
        UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Disposes of the whole remaining balance: the quantity leaves the batch in the same step as the
    /// outcome is recorded, and the caller writes one movement for the quantity this returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is the disposal half of a write-off; <see cref="WriteOff"/> only blocks a batch. It is
    /// composed here rather than built from <see cref="DecreaseQuantity"/>, because a decrease that
    /// empties a batch marks it <see cref="BatchStatus.Depleted"/> — a batch whose stock was destroyed
    /// must say so — and a batch already blocked by <see cref="WriteOff"/> refuses to be decreased at
    /// all, so no sequence of the existing operations could dispose of one.
    /// </para>
    /// <para>
    /// The whole balance is disposed of, never part of it: a batch that has lost some of its stock is
    /// adjusted (with a reason), a batch whose stock has left for good is written off.
    /// </para>
    /// </remarks>
    /// <returns>The quantity that left the ledger, for the caller's movement.</returns>
    public decimal WriteOffRemainingStock(
        string reason,
        BatchStatus status = BatchStatus.WrittenOff,
        DateTimeOffset? nowUtc = null)
    {
        if (status is not (BatchStatus.WrittenOff or BatchStatus.Recalled))
        {
            throw new DomainValidationException($"'{status}' is not a valid write-off outcome.");
        }

        if (QuantityOnHand == 0m)
        {
            throw new DomainValidationException($"Batch '{BatchNumber}' has no quantity left to write off.");
        }

        StatusReason = Guard.Required(reason, StockMovement.ReasonMaxLength, "Reason");
        var writtenOff = QuantityOnHand;
        QuantityOnHand = 0m;
        Status = status;
        UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
        return writtenOff;
    }

    /// <summary>Recomputes the cached status for a batch that is simply past its expiry date.</summary>
    public bool RefreshExpiryStatus(DateOnly businessDate, DateTimeOffset? nowUtc = null)
    {
        if (Status != BatchStatus.Active || QuantityOnHand == 0m)
        {
            return false;
        }

        if (!ExpiryPolicy.IsExpired(ExpiryDate, businessDate))
        {
            return false;
        }

        Status = BatchStatus.Expired;
        StatusReason = $"Expired on {ExpiryDate:yyyy-MM-dd}.";
        UpdatedAtUtc = nowUtc ?? DateTimeOffset.UtcNow;
        return true;
    }

    private void EnsureMovable(BatchStatus blocked, string description)
    {
        if (Status == blocked)
        {
            throw new DomainValidationException($"Batch '{BatchNumber}' is {description} and cannot be moved.");
        }
    }
}
