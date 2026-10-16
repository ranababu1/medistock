namespace MediStock.Application.Suppliers;

/// <summary>
/// One quantity of one batch the operator is asking to send back: which batch, how much, and any note
/// that should travel with the line.
/// </summary>
public sealed record SupplierReturnLineDraft
{
    /// <summary>Far more than one supplier return ever carries.</summary>
    public const int MaxLines = 100;

    /// <summary>The batch the quantity leaves. Must have been bought from the supplier being credited.</summary>
    public int MedicineBatchId { get; init; }

    public decimal Quantity { get; init; }

    public string? Notes { get; init; }
}

/// <summary>
/// A supplier return as the operator has assembled it, before anything has been checked against the
/// batches it names.
/// </summary>
public sealed record SupplierReturnDraft
{
    /// <summary>The supplier the goods go back to, and the account the credit lands on.</summary>
    public int SupplierId { get; init; }

    public string ReturnedBy { get; init; } = string.Empty;

    /// <summary>Why the medicine is going back. Required, and shown on the return document.</summary>
    public string Reason { get; init; } = string.Empty;

    public string? Notes { get; init; }

    /// <summary>The credit note the supplier issued against this return, when the operator already has it.</summary>
    public string? SupplierCreditNoteNumber { get; init; }

    /// <summary>Business date the return counts towards. Null takes the current business date.</summary>
    public DateOnly? BusinessDate { get; init; }

    public IReadOnlyList<SupplierReturnLineDraft> Lines { get; init; } = [];

    /// <summary>
    /// Drops lines with no quantity or no batch, trims text and clamps the number of lines, so the
    /// catalog can treat a draft as already sane. Anything still wrong is a domain rule and is reported
    /// as such.
    /// </summary>
    public SupplierReturnDraft Normalise()
    {
        var lines = Lines
            .Where(line => line is not null)
            .Where(line => line.MedicineBatchId > 0 && line.Quantity > 0m)
            .Take(SupplierReturnLineDraft.MaxLines)
            .Select(line => line with { Notes = Blank(line.Notes) })
            .ToList();

        return this with
        {
            ReturnedBy = ReturnedBy?.Trim() ?? string.Empty,
            Reason = Reason?.Trim() ?? string.Empty,
            Notes = Blank(Notes),
            SupplierCreditNoteNumber = Blank(SupplierCreditNoteNumber),
            Lines = lines,
        };

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}