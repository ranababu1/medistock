namespace MediStock.Domain.Inventory;

/// <summary>
/// The single place that decides whether a batch may be dispensed. See
/// <c>docs/decisions/0002-expiry-date-interpretation.md</c>.
/// </summary>
/// <remarks>
/// MediStock treats <c>ExpiryDate</c> as the <b>last day on which the batch may be dispensed</b>.
/// A batch is therefore sellable while <c>businessDate &lt;= ExpiryDate</c> and counts as expired
/// from the following day. Pharmacies commonly print only a month ("EXP 03/2027"); the last day
/// of that month is stored. This interpretation must be confirmed with the pharmacy operator
/// before real use.
/// </remarks>
public static class ExpiryPolicy
{
    public static bool IsExpired(DateOnly expiryDate, DateOnly businessDate) => businessDate > expiryDate;

    public static bool IsSellable(BatchStatus status, DateOnly expiryDate, decimal quantityOnHand, DateOnly businessDate)
        => status == BatchStatus.Active
           && quantityOnHand > 0m
           && !IsExpired(expiryDate, businessDate);

    public static bool IsSellable(MedicineBatch batch, DateOnly businessDate)
    {
        ArgumentNullException.ThrowIfNull(batch);
        return IsSellable(batch.Status, batch.ExpiryDate, batch.QuantityOnHand, businessDate);
    }

    /// <summary>
    /// True while a batch could have returned stock put back on its shelf. A depleted batch qualifies:
    /// the commonest return is a pack from a batch that has since sold out, and it belongs back in the
    /// same batch so its expiry date still governs it.
    /// </summary>
    public static bool CanBeRestocked(BatchStatus status, DateOnly expiryDate, DateOnly businessDate)
        => status is BatchStatus.Active or BatchStatus.Depleted && !IsExpired(expiryDate, businessDate);

    /// <summary>Days remaining before expiry; negative once the batch has expired.</summary>
    public static int DaysUntilExpiry(DateOnly expiryDate, DateOnly businessDate)
        => expiryDate.DayNumber - businessDate.DayNumber;
}
