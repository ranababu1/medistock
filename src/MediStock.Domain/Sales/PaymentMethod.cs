namespace MediStock.Domain.Sales;

/// <summary>
/// How a customer settled an invoice. Values are persisted and must stay stable; new methods are
/// appended so existing rows keep their meaning.
/// </summary>
public enum PaymentMethod
{
    Cash = 1,
    Card = 2,
    Upi = 3,
    Other = 4,
}
