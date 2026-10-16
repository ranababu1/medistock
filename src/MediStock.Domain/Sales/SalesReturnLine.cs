using MediStock.Domain.Common;
using MediStock.Domain.Inventory;

namespace MediStock.Domain.Sales;

/// <summary>
/// One quantity of one batch coming back from one invoice line, with the money refunded for it and the
/// disposition the operator chose.
/// </summary>
/// <remarks>
/// The line points at both the invoice line it reverses (<see cref="SaleLineId"/>) and the batch the
/// stock came out of (<see cref="MedicineBatchId"/>). Together they are what allows a return to be
/// audited: which invoice, which line, which batch, how much money, and what happened to the goods.
/// </remarks>
public class SalesReturnLine
{
    public const int NotesMaxLength = 300;

    /// <summary>Required by Entity Framework Core.</summary>
    private SalesReturnLine()
    {
    }

    public int Id { get; private set; }

    public int SalesReturnId { get; private set; }

    public SalesReturn SalesReturn { get; private set; } = null!;

    /// <summary>Position on the return document, starting at one.</summary>
    public int LineNumber { get; private set; }

    /// <summary>The invoice line this quantity is credited against.</summary>
    public int SaleLineId { get; private set; }

    public SaleLine SaleLine { get; private set; } = null!;

    /// <summary>The batch the returned quantity goes back into, when the disposition is a restock.</summary>
    public int MedicineBatchId { get; private set; }

    public MedicineBatch MedicineBatch { get; private set; } = null!;

    public decimal Quantity { get; private set; }

    /// <summary>
    /// Money handed back for this quantity, including the tax that was charged on it. The last
    /// quantity to clear an invoice line receives the line's exact remainder, so the refunds on a fully
    /// returned line always add back up to what was charged for it.
    /// </summary>
    public decimal RefundAmount { get; private set; }

    /// <summary>Tax contained in <see cref="RefundAmount"/>; the rest is revenue taken back.</summary>
    public decimal TaxAmount { get; private set; }

    /// <summary>Cost of the stock that came back, taken from the batch it was sold from.</summary>
    public decimal CostAmount { get; private set; }

    public ReturnDisposition Disposition { get; private set; }

    public string? Notes { get; private set; }

    /// <summary>True when this quantity went back onto the shelf.</summary>
    public bool WasRestocked => Disposition == ReturnDisposition.Restock;

    internal static SalesReturnLine Create(
        int lineNumber,
        int saleLineId,
        int medicineBatchId,
        decimal quantity,
        decimal refundAmount,
        decimal taxAmount,
        decimal costAmount,
        ReturnDisposition disposition,
        string? notes = null)
    {
        if (lineNumber <= 0)
        {
            throw new DomainValidationException("A return line must have a line number of at least one.");
        }

        if (saleLineId <= 0)
        {
            throw new DomainValidationException("A return line must reference an invoice line.");
        }

        if (medicineBatchId <= 0)
        {
            throw new DomainValidationException("A return line must reference the batch the stock came from.");
        }

        if (!Enum.IsDefined(disposition))
        {
            throw new DomainValidationException($"'{disposition}' is not a valid return disposition.");
        }

        return new SalesReturnLine
        {
            LineNumber = lineNumber,
            SaleLineId = saleLineId,
            MedicineBatchId = medicineBatchId,
            Quantity = Guard.Positive(quantity, "Returned quantity"),
            RefundAmount = Money.Round(Guard.NonNegative(refundAmount, "Refund amount")),
            TaxAmount = Money.Round(Guard.NonNegative(taxAmount, "Refunded tax")),
            CostAmount = Money.Round(Guard.NonNegative(costAmount, "Returned cost")),
            Disposition = disposition,
            Notes = Guard.Optional(notes, NotesMaxLength, "Line notes"),
        };
    }
}
