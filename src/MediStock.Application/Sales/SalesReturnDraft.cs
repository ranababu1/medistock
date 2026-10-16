using MediStock.Domain.Sales;

namespace MediStock.Application.Sales;

/// <summary>
/// One quantity the operator is asking to take back: a line of the invoice, the batch the pack came
/// from, how much, and what is to be done with it.
/// </summary>
public sealed record SalesReturnLineDraft
{
    /// <summary>Far more than one customer brings back at once.</summary>
    public const int MaxLines = 100;

    /// <summary>Line number on the original invoice.</summary>
    public int LineNumber { get; init; }

    /// <summary>The batch the quantity came out of, as printed on the pack.</summary>
    public int MedicineBatchId { get; init; }

    public decimal Quantity { get; init; }

    /// <summary>What happens to the goods. Restocking has to be chosen; it is never assumed.</summary>
    public ReturnDisposition Disposition { get; init; } = ReturnDisposition.Quarantine;

    public string? Notes { get; init; }
}

/// <summary>
/// A return as the operator has assembled it, before anything has been checked against the invoice.
/// </summary>
public sealed record SalesReturnDraft
{
    /// <summary>The invoice being returned against.</summary>
    public int SaleId { get; init; }

    public string ReturnedBy { get; init; } = string.Empty;

    /// <summary>Why the customer brought the medicine back. Shown on the return document.</summary>
    public string Reason { get; init; } = string.Empty;

    public string? Notes { get; init; }

    /// <summary>
    /// How the money is handed back, which decides whether the day's cash takes the hit. Defaults to
    /// cash, the usual counter refund.
    /// </summary>
    public PaymentMethod RefundMethod { get; init; } = PaymentMethod.Cash;

    /// <summary>
    /// Business date the refund counts towards. Null takes the current business date, which is what a
    /// counter return almost always is.
    /// </summary>
    public DateOnly? BusinessDate { get; init; }

    public IReadOnlyList<SalesReturnLineDraft> Lines { get; init; } = [];

    /// <summary>
    /// Drops requests with no quantity, trims text and clamps the number of lines, so the catalog can
    /// treat a draft as already sane. Anything still wrong is a domain rule and is reported as such.
    /// </summary>
    public SalesReturnDraft Normalise()
    {
        var lines = Lines
            .Where(line => line is not null)
            .Where(line => line.LineNumber > 0 && line.MedicineBatchId > 0 && line.Quantity > 0m)
            .Take(SalesReturnLineDraft.MaxLines)
            .Select(line => line with { Notes = Blank(line.Notes) })
            .ToList();

        return this with
        {
            ReturnedBy = ReturnedBy?.Trim() ?? string.Empty,
            Reason = Reason?.Trim() ?? string.Empty,
            Notes = Blank(Notes),
            Lines = lines,
        };

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
