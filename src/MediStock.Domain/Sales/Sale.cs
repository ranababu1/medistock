using MediStock.Domain.Common;

namespace MediStock.Domain.Sales;

/// <summary>
/// A completed invoice: the immutable record of what left the shelf, what was charged for it and how
/// it was paid for.
/// </summary>
/// <remarks>
/// <para>
/// A sale is created completed, in one act, and is never edited. Corrections are future return or
/// cancellation documents that reference it rather than rewriting it, which is why every money
/// column is stored instead of derived.
/// </para>
/// <para>
/// Totals are the sum of the rounded line values, not a rounding of the unrounded sum. A printed
/// invoice whose lines add up to a different total than the total line would be indefensible, and
/// this way they cannot disagree.
/// </para>
/// <para>
/// The payments must settle the invoice exactly: their sum must equal <see cref="Total"/>. Change
/// handed over at the counter is a till concern, not an invoice line. See
/// <c>docs/decisions/0007-point-of-sale-pricing-and-settlement.md</c>.
/// </para>
/// </remarks>
public class Sale
{
    public const int InvoiceNumberMaxLength = 32;
    public const int OperatorMaxLength = 150;
    public const int CustomerNameMaxLength = 150;
    public const int CustomerPhoneMaxLength = 32;
    public const int NotesMaxLength = 500;

    /// <summary>Required by Entity Framework Core.</summary>
    private Sale()
    {
    }

    public int Id { get; private set; }

    /// <summary>
    /// Invoice number issued to the customer. Unique across the database, and never reused.
    /// </summary>
    public string InvoiceNumber { get; private set; } = string.Empty;

    /// <summary>Local business date the sale counts towards in daily reports.</summary>
    public DateOnly BusinessDate { get; private set; }

    public DateTimeOffset SoldAtUtc { get; private set; }

    /// <summary>Operator who took the sale, until named users arrive in Milestone 5.</summary>
    public string SoldBy { get; private set; } = string.Empty;

    public string? CustomerName { get; private set; }

    public string? CustomerPhone { get; private set; }

    public string? Notes { get; private set; }

    public SaleStatus Status { get; private set; } = SaleStatus.Completed;

    /// <summary>Sum of the line gross amounts, before discounts and tax.</summary>
    public decimal SubTotal { get; private set; }

    public decimal DiscountTotal { get; private set; }

    /// <summary>Sum of the line net amounts, i.e. <see cref="SubTotal"/> less <see cref="DiscountTotal"/>.</summary>
    public decimal TaxableTotal { get; private set; }

    public decimal TaxTotal { get; private set; }

    /// <summary>What the customer owed, and what the payments settle.</summary>
    public decimal Total { get; private set; }

    /// <summary>Cost of the goods sold, for margin reporting.</summary>
    public decimal CostTotal { get; private set; }

    public DateTimeOffset CreatedAtUtc { get; private set; }

    public ICollection<SaleLine> Lines { get; private set; } = new List<SaleLine>();

    public ICollection<Payment> Payments { get; private set; } = new List<Payment>();

    /// <summary>Revenue less tax and the cost of the goods sold.</summary>
    public decimal Margin => Money.Round(Total - TaxTotal - CostTotal);

    /// <summary>Total quantity of units on the invoice.</summary>
    public decimal UnitsSold => Lines.Sum(l => l.Quantity);

    public static Sale Complete(
        string invoiceNumber,
        DateOnly businessDate,
        string soldBy,
        IReadOnlyList<SaleLine> lines,
        IReadOnlyList<Payment> payments,
        string? customerName = null,
        string? customerPhone = null,
        string? notes = null,
        DateTimeOffset? nowUtc = null)
    {
        ArgumentNullException.ThrowIfNull(lines);
        ArgumentNullException.ThrowIfNull(payments);

        if (lines.Count == 0)
        {
            throw new DomainValidationException("A sale must have at least one line.");
        }

        if (payments.Count == 0)
        {
            throw new DomainValidationException("A sale must record how it was paid for.");
        }

        var timestamp = nowUtc ?? DateTimeOffset.UtcNow;

        var sale = new Sale
        {
            InvoiceNumber = Guard.Required(invoiceNumber, InvoiceNumberMaxLength, "Invoice number"),
            BusinessDate = businessDate,
            SoldAtUtc = timestamp,
            SoldBy = Guard.Required(soldBy, OperatorMaxLength, "Operator"),
            CustomerName = Guard.Optional(customerName, CustomerNameMaxLength, "Customer name"),
            CustomerPhone = Guard.Optional(customerPhone, CustomerPhoneMaxLength, "Customer phone"),
            Notes = Guard.Optional(notes, NotesMaxLength, "Notes"),
            Status = SaleStatus.Completed,
            CreatedAtUtc = timestamp,
        };

        var lineNumber = 1;
        foreach (var line in lines)
        {
            if (line.LineNumber != lineNumber)
            {
                throw new DomainValidationException(
                    $"Sale lines must be numbered consecutively from one; expected line {lineNumber} but found {line.LineNumber}.");
            }

            sale.Lines.Add(line);
            lineNumber++;
        }

        foreach (var payment in payments)
        {
            sale.Payments.Add(payment);
        }

        var totals = SaleTotals.From(lines);
        sale.SubTotal = totals.SubTotal;
        sale.DiscountTotal = totals.DiscountTotal;
        sale.TaxableTotal = totals.TaxableTotal;
        sale.TaxTotal = totals.TaxTotal;
        sale.Total = totals.Total;
        sale.CostTotal = totals.CostTotal;

        var settled = Money.Round(payments.Sum(p => p.Amount));
        if (settled != sale.Total)
        {
            throw new DomainValidationException(
                $"Payments of {settled:0.00} do not settle an invoice total of {sale.Total:0.00}.");
        }

        return sale;
    }
}
