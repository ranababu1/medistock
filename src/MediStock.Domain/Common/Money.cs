namespace MediStock.Domain.Common;

/// <summary>
/// Rounding policy for monetary amounts. MediStock stores money as <see cref="decimal"/> with two
/// decimal places on SQL Server and rounds half away from zero (the retail convention), never
/// banker's rounding, so till totals and printed invoices agree with manual arithmetic.
/// </summary>
public static class Money
{
    public const int DecimalPlaces = 2;

    public const MidpointRounding Rounding = MidpointRounding.AwayFromZero;

    public static decimal Round(decimal amount) => Math.Round(amount, DecimalPlaces, Rounding);

    /// <summary>
    /// The part of <paramref name="total"/> that <paramref name="part"/> of <paramref name="whole"/>
    /// represents, rounded once.
    /// </summary>
    /// <remarks>
    /// Used to split a charged amount — a line total, its tax, its cost — across the quantities
    /// returned from it. Dividing and rounding each share independently drifts by a paisa, so callers
    /// hand the last share to <see cref="RemainderShare"/> instead and the parts then add back up to
    /// the whole exactly.
    /// </remarks>
    public static decimal ProportionalShare(decimal total, decimal part, decimal whole)
    {
        if (whole == 0m)
        {
            throw new ArgumentOutOfRangeException(nameof(whole), "A share cannot be taken of nothing.");
        }

        return Round(total * part / whole);
    }

    /// <summary>What is left of <paramref name="total"/> once <paramref name="allocated"/> has been handed out.</summary>
    public static decimal RemainderShare(decimal total, decimal allocated) => Round(total - allocated);

    /// <summary>
    /// Splits one figure charged on an invoice line — its total, its tax, its cost — across a quantity
    /// being given back to the customer.
    /// </summary>
    /// <remarks>
    /// The share is proportional while units are still out with the customer, and the entry that clears
    /// the line takes the exact remainder of everything charged so far. That keeps a fully returned line
    /// giving back precisely what it charged, with no drifting paisa. Both the recorded return and the
    /// figure the counter shows before taking the goods back come through here, so a preview cannot
    /// promise a refund the return would not pay.
    /// </remarks>
    public static decimal ShareOfLine(
        decimal total,
        decimal alreadyShared,
        decimal quantity,
        decimal lineQuantity,
        decimal returnableQuantity)
        => quantity == returnableQuantity
            ? RemainderShare(total, alreadyShared)
            : ProportionalShare(total, quantity, lineQuantity);
}
