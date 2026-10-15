using MediStock.Domain.Catalogue;
using MediStock.Domain.Common;
using MediStock.Domain.Inventory;

namespace MediStock.Domain.Purchasing;

/// <summary>
/// One ordered medicine on a purchase, and — once the purchase is received — the batch it created.
/// </summary>
public sealed class PurchaseLine
{
    public const int BatchNumberMaxLength = 64;

    // EF Core materialisation only.
    private PurchaseLine()
    {
        BatchNumber = string.Empty;
    }

    private PurchaseLine(
        int medicineId,
        string batchNumber,
        DateOnly expiryDate,
        decimal quantity,
        decimal unitCost,
        decimal sellingPrice)
    {
        MedicineId = medicineId;
        BatchNumber = batchNumber;
        ExpiryDate = expiryDate;
        Quantity = quantity;
        UnitCost = unitCost;
        SellingPrice = sellingPrice;
    }

    public int Id { get; private set; }

    public int PurchaseId { get; private set; }

    public Purchase? Purchase { get; private set; }

    public int MedicineId { get; private set; }

    public Medicine? Medicine { get; private set; }

    public string BatchNumber { get; private set; }

    public DateOnly ExpiryDate { get; private set; }

    /// <summary>Quantity ordered. Immutable once the purchase is received.</summary>
    public decimal Quantity { get; private set; }

    /// <summary>
    /// Quantity actually received. Zero while the purchase is a draft, and equal to
    /// <see cref="Quantity"/> from receiving onwards. Modelled separately from <see cref="Quantity"/>
    /// so partial receiving can be added later without a schema change.
    /// </summary>
    public decimal QuantityReceived { get; private set; }

    /// <summary>Purchase cost per unit — the value used to cost stock and to value inventory.</summary>
    public decimal UnitCost { get; private set; }

    public decimal SellingPrice { get; private set; }

    /// <summary>
    /// The batch this line created. Retained after a reversal so the purchase stays traceable to the
    /// stock it once brought in.
    /// </summary>
    public int? MedicineBatchId { get; private set; }

    public MedicineBatch? MedicineBatch { get; private set; }

    /// <summary>Ordered line value at cost. Rounded once, at the line, so header and line totals agree.</summary>
    public decimal LineTotal => Money.Round(Quantity * UnitCost);

    internal static PurchaseLine Create(
        int medicineId,
        string batchNumber,
        DateOnly expiryDate,
        decimal quantity,
        decimal unitCost,
        decimal sellingPrice)
    {
        if (medicineId <= 0)
        {
            throw new DomainValidationException("A purchase line must reference an existing medicine.");
        }

        Guard.Positive(quantity, "Purchase quantity");
        Guard.NonNegative(unitCost, "Unit cost");
        Guard.NonNegative(sellingPrice, "Selling price");

        return new PurchaseLine(
            medicineId,
            Guard.Required(batchNumber, BatchNumberMaxLength, "Batch number"),
            expiryDate,
            quantity,
            Money.Round(unitCost),
            Money.Round(sellingPrice));
    }

    /// <summary>Marks the line as fully received.</summary>
    internal void MarkReceived()
    {
        QuantityReceived = Quantity;
    }

    /// <summary>Links the line to the batch created for it. The batch must already be persisted.</summary>
    internal void AttachBatch(MedicineBatch batch)
    {
        ArgumentNullException.ThrowIfNull(batch);

        if (batch.Id <= 0)
        {
            throw new InvalidOperationException("The batch must be saved before it can be linked to a purchase line.");
        }

        MedicineBatchId = batch.Id;
        MedicineBatch = batch;
    }
}
