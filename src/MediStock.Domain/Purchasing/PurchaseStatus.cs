namespace MediStock.Domain.Purchasing;

/// <summary>
/// Lifecycle of a purchase. Values are persisted and must stay stable.
/// </summary>
/// <remarks>
/// The progression is deliberately one-way. Goods are only ever added to stock by moving a purchase
/// from <see cref="Draft"/> to <see cref="Received"/>, and a received purchase is never deleted —
/// it is <see cref="Reversed"/>, which leaves both the original receipt and its reversal in the
/// ledger.
/// </remarks>
public enum PurchaseStatus
{
    /// <summary>Being prepared. Nothing has reached stock yet and lines may still be edited.</summary>
    Draft = 1,

    /// <summary>Confirmed. Every line created a batch and a receipt movement.</summary>
    Received = 2,

    /// <summary>Received by mistake and undone. The batches and their reversal movements remain.</summary>
    Reversed = 3,

    /// <summary>Abandoned before receiving. Nothing ever reached stock.</summary>
    Cancelled = 4,
}
