using MediStock.Domain.Common;

namespace MediStock.Domain.Sales;

/// <summary>
/// One settlement against an invoice. Several payments may settle one sale, which is how a split
/// payment (part cash, part card) is recorded.
/// </summary>
public class Payment
{
    public const int ReferenceMaxLength = 100;

    /// <summary>Required by Entity Framework Core.</summary>
    private Payment()
    {
    }

    public int Id { get; private set; }

    public int SaleId { get; private set; }

    public Sale Sale { get; private set; } = null!;

    public PaymentMethod Method { get; private set; }

    /// <summary>
    /// Amount settled by this method, not the amount tendered. See
    /// <c>docs/decisions/0007-point-of-sale-pricing-and-settlement.md</c>.
    /// </summary>
    public decimal Amount { get; private set; }

    /// <summary>Card or UPI reference, or any note the operator needs to trace the payment.</summary>
    public string? Reference { get; private set; }

    public DateTimeOffset PaidAtUtc { get; private set; }

    public static Payment Record(
        PaymentMethod method,
        decimal amount,
        string? reference = null,
        DateTimeOffset? nowUtc = null)
    {
        if (!Enum.IsDefined(method))
        {
            throw new DomainValidationException($"'{method}' is not a supported payment method.");
        }

        return new Payment
        {
            Method = method,
            Amount = Money.Round(Guard.Positive(amount, "Payment amount")),
            Reference = Guard.Optional(reference, ReferenceMaxLength, "Payment reference"),
            PaidAtUtc = nowUtc ?? DateTimeOffset.UtcNow,
        };
    }
}
