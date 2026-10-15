using MediStock.Domain.Sales;

namespace MediStock.Application.Sales;

/// <summary>
/// One line the operator has rung up, before any batch has been chosen. Prices and the tax rate are
/// optional: leaving them null means "use whatever the catalogue and the batch say", which is what
/// the scan-a-product path does.
/// </summary>
public sealed record SaleLineDraft
{
    /// <summary>Far more than a pharmacy counter rings up in one customer's basket.</summary>
    public const int MaxLines = 200;

    public int MedicineId { get; init; }

    public decimal Quantity { get; init; } = 1m;

    /// <summary>Price per unit to charge. Null takes the selling price of the batches consumed.</summary>
    public decimal? UnitPrice { get; init; }

    /// <summary>Absolute discount on this line, before tax.</summary>
    public decimal DiscountAmount { get; init; }

    /// <summary>Tax rate to apply. Null takes the rate configured on the medicine.</summary>
    public decimal? TaxRatePercent { get; init; }

    /// <summary>The operator asked for a specific batch, for example when a customer insists.</summary>
    public int? PreferredBatchId { get; init; }
}

/// <summary>How the customer is settling, split across as many methods as they use.</summary>
public sealed record PaymentDraft
{
    public PaymentMethod Method { get; init; } = PaymentMethod.Cash;

    public decimal Amount { get; init; }

    public string? Reference { get; init; }
}

/// <summary>
/// A basket as the operator has assembled it. This is a request, not a document: nothing here has
/// been validated against stock or the catalogue yet.
/// </summary>
public sealed record SaleDraft
{
    public string SoldBy { get; init; } = string.Empty;

    public string? CustomerName { get; init; }

    public string? CustomerPhone { get; init; }

    public string? Notes { get; init; }

    public IReadOnlyList<SaleLineDraft> Lines { get; init; } = [];

    public IReadOnlyList<PaymentDraft> Payments { get; init; } = [];

    /// <summary>
    /// Drops lines that ask for no quantity, trims text, and clamps line count, so the catalog can
    /// treat a draft as already sane. Anything still wrong is a domain rule and is reported as such.
    /// </summary>
    public SaleDraft Normalise()
    {
        var lines = Lines
            .Where(line => line is not null)
            .Where(line => line.MedicineId > 0 && line.Quantity > 0m)
            .Take(SaleLineDraft.MaxLines)
            .Select(line => line with
            {
                DiscountAmount = line.DiscountAmount < 0m ? 0m : line.DiscountAmount,
                TaxRatePercent = line.TaxRatePercent is < 0m ? null : line.TaxRatePercent,
            })
            .ToList();

        return this with
        {
            SoldBy = SoldBy?.Trim() ?? string.Empty,
            CustomerName = Blank(CustomerName),
            CustomerPhone = Blank(CustomerPhone),
            Notes = Blank(Notes),
            Lines = lines,
            Payments = Payments
                .Where(payment => payment is not null && payment.Amount > 0m)
                .ToList(),
        };

        static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
    }
}
