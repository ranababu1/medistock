namespace MediStock.Domain.Sales;

/// <summary>
/// Lifecycle of a completed sale. Values are persisted and must stay stable.
/// </summary>
/// <remarks>
/// A sale is created <see cref="Completed"/>; it is never edited afterwards. Milestone 4 introduces
/// returns and cancellations, which move the status to <see cref="Returned"/> or
/// <see cref="Cancelled"/> while leaving the original lines and payments untouched.
/// </remarks>
public enum SaleStatus
{
    Completed = 1,
    Returned = 2,
    Cancelled = 3,
}
