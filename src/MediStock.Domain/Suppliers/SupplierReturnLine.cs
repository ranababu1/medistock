using MediStock.Domain.Common;
using MediStock.Domain.Inventory;

namespace MediStock.Domain.Suppliers;

/// <summary>
/// One quantity of one batch going back to its supplier, costed at what the pharmacy paid for it.
/// </summary>
public class SupplierReturnLine
{
    public const int NotesMaxLength = 300;

    /// <summary>Required by Entity Framework Core.</summary>
    private SupplierReturnLine()
    {
    }

    public int Id { get; private set; }

    public int SupplierReturnId { get; private set; }

    public SupplierReturn SupplierReturn { get; private set; } = null!;

    /// <summary>Position on the return document, starting at one.</summary>
    public int LineNumber { get; private set; }

    /// <summary>The batch the quantity leaves.</summary>
    public int MedicineBatchId { get; private set; }

    public MedicineBatch MedicineBatch { get; private set; } = null!;

    public decimal Quantity { get; private set; }

    /// <summary>
    /// What the pharmacy paid for one unit of this batch, copied onto the line so the credit survives
    /// even if the batch's cost is later corrected.
    /// </summary>
    public decimal UnitCost { get; private set; }

    /// <summary>The value of the quantity at cost: what the supplier is expected to credit.</summary>
    public decimal CostValue { get; private set; }

    public string? Notes { get; private set; }

    internal static SupplierReturnLine Create(
        int lineNumber,
        int medicineBatchId,
        decimal quantity,
        decimal unitCost,
        string? notes = null)
    {
        if (lineNumber <= 0)
        {
            throw new DomainValidationException("A supplier return line must have a line number of at least one.");
        }

        if (medicineBatchId <= 0)
        {
            throw new DomainValidationException("A supplier return line must reference the batch the stock leaves.");
        }

        var cost = Money.Round(Guard.NonNegative(unitCost, "Unit cost"));
        var quantityOut = Guard.Positive(quantity, "Returned quantity");

        return new SupplierReturnLine
        {
            LineNumber = lineNumber,
            MedicineBatchId = medicineBatchId,
            Quantity = quantityOut,
            UnitCost = cost,
            CostValue = Money.Round(quantityOut * cost),
            Notes = Guard.Optional(notes, NotesMaxLength, "Line notes"),
        };
    }
}