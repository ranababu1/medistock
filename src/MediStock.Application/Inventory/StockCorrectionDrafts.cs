using MediStock.Domain.Inventory;

namespace MediStock.Application.Inventory;

/// <summary>
/// The shelf disagrees with the ledger and the operator is correcting it by hand: which batch, which
/// way, by how much, and why.
/// </summary>
/// <remarks>
/// Part of a batch is an adjustment; disposing of all of it for ever is a write-off (see
/// <see cref="StockWriteOffDraft"/>). The distinction is stated on screen because it decides whether
/// the stock can ever be sold again.
/// </remarks>
public sealed record StockAdjustmentDraft
{
    public int MedicineBatchId { get; init; }

    /// <summary>True adds stock the ledger did not know about; false removes stock nobody had recorded losing.</summary>
    public bool IsIncrease { get; init; }

    public decimal Quantity { get; init; }

    /// <summary>Required. The audit trail and the movement ledger both show it.</summary>
    public string Reason { get; init; } = string.Empty;

    public string? Notes { get; init; }

    /// <summary>Operator name the movement is signed with. Defaults to the signed-in account on the screen.</summary>
    public string AdjustedBy { get; init; } = string.Empty;

    /// <summary>
    /// Business date the correction counts towards. Null takes the current business date: a correction is
    /// booked when it is discovered, not when the mistake happened.
    /// </summary>
    public DateOnly? BusinessDate { get; init; }

    /// <summary>
    /// Trims text and refuses nothing, so the catalog can treat a draft as already sane. Anything still
    /// wrong is a domain rule and is reported as such.
    /// </summary>
    public StockAdjustmentDraft Normalise()
        => this with
        {
            Reason = Reason?.Trim() ?? string.Empty,
            Notes = Blank(Notes),
            AdjustedBy = AdjustedBy?.Trim() ?? string.Empty,
        };

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// The remaining stock of a batch leaves for good: destroyed, expired and disposed of, or sent out
/// under a recall notice.
/// </summary>
/// <remarks>
/// Disposal always takes the <strong>whole</strong> remaining quantity — a partial loss is an
/// adjustment. The batch is left blocked with the reason recorded on it, so nothing from it can be
/// sold, restocked or returned to the supplier afterwards.
/// </remarks>
public sealed record StockWriteOffDraft
{
    public int MedicineBatchId { get; init; }

    /// <summary>
    /// True is a recall disposal: the batch is marked <see cref="BatchStatus.Recalled"/> and the movement
    /// is a <see cref="StockMovementType.RecallWriteOff"/>. False is an expiry or damage disposal, which
    /// marks the batch <see cref="BatchStatus.WrittenOff"/> and moves
    /// <see cref="StockMovementType.ExpiryWriteOff"/>.
    /// </summary>
    public bool IsRecall { get; init; }

    /// <summary>Required. The recall notice, the expiry decision or the damage report the operator is acting on.</summary>
    public string Reason { get; init; } = string.Empty;

    public string? Notes { get; init; }

    public string WrittenOffBy { get; init; } = string.Empty;

    /// <summary>Business date the disposal counts towards. Null takes the current business date.</summary>
    public DateOnly? BusinessDate { get; init; }

    public StockWriteOffDraft Normalise()
        => this with
        {
            Reason = Reason?.Trim() ?? string.Empty,
            Notes = Blank(Notes),
            WrittenOffBy = WrittenOffBy?.Trim() ?? string.Empty,
        };

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}

/// <summary>
/// A batch's stock is held out of sale where it sits: a recall notice has arrived, or the stock has been
/// condemned, and disposal is a separate step once the goods actually leave.
/// </summary>
/// <remarks>
/// Quarantining moves no quantity - nothing is sold and nothing leaves - but the batch is blocked with the
/// reason recorded on it, so nothing from it can be sold, restocked, adjusted or returned to the supplier
/// until it is written off. See <c>docs/decisions/0011-stock-corrections.md</c>.
/// </remarks>
public sealed record StockQuarantineDraft
{
    public int MedicineBatchId { get; init; }

    /// <summary>
    /// True is a recall hold: the batch is marked <see cref="BatchStatus.Recalled"/>. False is a write-off
    /// hold pending disposal, which marks the batch <see cref="BatchStatus.WrittenOff"/> with its stock
    /// still on it.
    /// </summary>
    public bool IsRecall { get; init; }

    /// <summary>Required. The recall notice, the condemnation or the damage report being acted on.</summary>
    public string Reason { get; init; } = string.Empty;

    public string? Notes { get; init; }

    /// <summary>Operator name the hold is signed with. Defaults to the signed-in account on the screen.</summary>
    public string QuarantinedBy { get; init; } = string.Empty;

    public StockQuarantineDraft Normalise()
        => this with
        {
            Reason = Reason?.Trim() ?? string.Empty,
            Notes = Blank(Notes),
            QuarantinedBy = QuarantinedBy?.Trim() ?? string.Empty,
        };

    static string? Blank(string? value) => string.IsNullOrWhiteSpace(value) ? null : value.Trim();
}