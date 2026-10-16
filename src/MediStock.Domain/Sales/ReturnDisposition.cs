namespace MediStock.Domain.Sales;

/// <summary>
/// What happened to the medicine a customer brought back. The disposition is the pharmacy's decision,
/// recorded explicitly, and it is the only thing that decides whether stock moves back onto the shelf.
/// </summary>
/// <remarks>
/// <para>
/// Returned medicine is never silently made sellable again. A pack that has been out of the pharmacy is
/// of unknown storage history, so an operator must state what is to be done with it — the same rule the
/// plan sets out for returns and write-offs. Only <see cref="Restock"/> increases a batch balance;
/// <see cref="Quarantine"/> and <see cref="Destroy"/> record the quantity for reporting and leave the
/// stock ledger alone.
/// </para>
/// <para>
/// Values are persisted, so they must not be renumbered. See
/// <c>docs/decisions/0008-sales-returns-and-stock-disposition.md</c>.
/// </para>
/// </remarks>
public enum ReturnDisposition
{
    /// <summary>Put back into the batch the quantity was sold from, sellable immediately.</summary>
    Restock = 1,

    /// <summary>Held aside, not sellable, pending a decision or a supplier claim.</summary>
    Quarantine = 2,

    /// <summary>Written off: damaged, contaminated, or otherwise unfit to dispense.</summary>
    Destroy = 3,
}
