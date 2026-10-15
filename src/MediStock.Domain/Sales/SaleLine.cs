using MediStock.Domain.Common;
using MediStock.Domain.Inventory;

namespace MediStock.Domain.Sales;

/// <summary>
/// One charged line of an invoice. Everything the printed document needs is copied here at the
/// moment of sale: the medicine's name, the price charged, the cost it came from, the tax rate
/// applied and the batches the quantity came out of.
/// </summary>
/// <remarks>
/// <para>
/// The money columns are stored rather than recomputed, because an invoice is a historical document:
/// if a tax rate, a catalogue name or the rounding policy changes, an old invoice must still reprint
/// exactly as it was issued.
/// </para>
/// <para>
/// The order of calculation is fixed and tested:
/// </para>
/// <list type="number">
/// <item><description><see cref="GrossAmount"/> = <c>round(quantity × unit price)</c></description></item>
/// <item><description><see cref="DiscountAmount"/> is an absolute amount, never more than the gross</description></item>
/// <item><description><see cref="TaxableAmount"/> = gross − discount, and is not rounded again</description></item>
/// <item><description><see cref="TaxAmount"/> = <c>round(taxable × rate ÷ 100)</c></description></item>
/// <item><description><see cref="LineTotal"/> = taxable + tax</description></item>
/// </list>
/// <para>
/// Rounding happens once per step, so a line printed on its own adds up the same way it does on the
/// invoice. See <c>docs/decisions/0007-point-of-sale-pricing-and-settlement.md</c>.
/// </para>
/// </remarks>
public class SaleLine
{
    public const int MedicineNameMaxLength = 200;
    public const decimal MaxTaxRatePercent = 100m;

    /// <summary>Required by Entity Framework Core.</summary>
    private SaleLine()
    {
    }

    public int Id { get; private set; }

    public int SaleId { get; private set; }

    public Sale Sale { get; private set; } = null!;

    /// <summary>Position on the invoice, starting at one.</summary>
    public int LineNumber { get; private set; }

    public int MedicineId { get; private set; }

    /// <summary>Medicine name as it stood when the sale was made.</summary>
    public string MedicineName { get; private set; } = string.Empty;

    public decimal Quantity { get; private set; }

    /// <summary>Price per unit actually charged, normally the batch selling price.</summary>
    public decimal UnitPrice { get; private set; }

    /// <summary>Cost per unit of the stock consumed, for margin reporting.</summary>
    public decimal UnitCost { get; private set; }

    public decimal GrossAmount { get; private set; }

    public decimal DiscountAmount { get; private set; }

    public decimal TaxableAmount { get; private set; }

    public decimal TaxRatePercent { get; private set; }

    public decimal TaxAmount { get; private set; }

    public decimal LineTotal { get; private set; }

    /// <summary>Which batches the quantity left. Their quantities sum to <see cref="Quantity"/>.</summary>
    public ICollection<SaleLineBatchAllocation> Allocations { get; private set; }
        = new List<SaleLineBatchAllocation>();

    /// <summary>Revenue less the cost of the goods sold, both taken from the stored line values.</summary>
    public decimal Margin => Money.Round(LineTotal - TaxAmount - LineCost);

    /// <summary>Cost of the stock this line consumed.</summary>
    public decimal LineCost => Money.Round(Quantity * UnitCost);

    /// <summary>
    /// Prices a line and attaches the batches it consumes. The allocation must cover the quantity
    /// exactly: short-picking is an <see cref="InsufficientStockException"/> raised by
    /// <see cref="FefoAllocator"/> before this point, and over-allocating is a bug.
    /// </summary>
    public static SaleLine Create(
        int lineNumber,
        int medicineId,
        string medicineName,
        decimal quantity,
        decimal unitPrice,
        decimal unitCost,
        decimal discountAmount,
        decimal taxRatePercent,
        IReadOnlyList<BatchAllocation> allocations)
    {
        if (lineNumber <= 0)
        {
            throw new DomainValidationException("A sale line must have a line number of at least one.");
        }

        if (medicineId <= 0)
        {
            throw new DomainValidationException("A sale line must reference an existing medicine.");
        }

        ArgumentNullException.ThrowIfNull(allocations);

        if (allocations.Count == 0)
        {
            throw new DomainValidationException(
                "A sale line must be allocated to at least one batch; nothing can be sold that did not come off the shelf.");
        }

        var soldQuantity = Guard.Positive(quantity, "Quantity");
        var price = Money.Round(Guard.NonNegative(unitPrice, "Unit price"));
        var cost = Money.Round(Guard.NonNegative(unitCost, "Unit cost"));

        if (taxRatePercent < 0m || taxRatePercent > MaxTaxRatePercent)
        {
            throw new DomainValidationException(
                $"Tax rate must be between 0 and {MaxTaxRatePercent:0.##} percent; {taxRatePercent:0.##} was given.");
        }

        var gross = Money.Round(soldQuantity * price);
        var discount = Money.Round(Guard.NonNegative(discountAmount, "Discount"));

        if (discount > gross)
        {
            throw new DomainValidationException(
                $"A discount of {discount:0.00} cannot exceed the line value of {gross:0.00}.");
        }

        var allocated = allocations.Sum(a => a.Quantity);
        if (allocated != soldQuantity)
        {
            throw new DomainValidationException(
                $"Line for '{medicineName}' sells {soldQuantity:0.###} but its batches account for {allocated:0.###}.");
        }

        var taxable = gross - discount;

        var line = new SaleLine
        {
            LineNumber = lineNumber,
            MedicineId = medicineId,
            MedicineName = Guard.Required(medicineName, MedicineNameMaxLength, "Medicine name"),
            Quantity = soldQuantity,
            UnitPrice = price,
            UnitCost = cost,
            GrossAmount = gross,
            DiscountAmount = discount,
            TaxableAmount = taxable,
            TaxRatePercent = Money.Round(taxRatePercent),
            TaxAmount = Money.Round(taxable * taxRatePercent / 100m),
            LineTotal = taxable + Money.Round(taxable * taxRatePercent / 100m),
        };

        // The allocation rows are created from the same record the allocator returned, so the batch
        // identity on the invoice cannot drift from the batch that was actually decremented.
        foreach (var allocation in allocations)
        {
            line.Allocations.Add(SaleLineBatchAllocation.From(allocation));
        }

        return line;
    }
}
