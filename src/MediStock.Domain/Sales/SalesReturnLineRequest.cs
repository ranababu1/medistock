namespace MediStock.Domain.Sales;

/// <summary>
/// What an operator is asking to take back: a quantity of one batch from one line of one invoice.
/// </summary>
/// <remarks>
/// The batch is named rather than derived. The pack in the customer's hand carries a printed batch
/// number and expiry date, so the operator can say exactly which stock is coming back; letting the
/// application guess would risk restocking an earlier-expiry batch than the one actually returned.
/// <see cref="SalesReturn.Record"/> checks the batch against the line's own allocations, so a request
/// can only name a batch that line really sold.
/// </remarks>
public sealed record SalesReturnLineRequest(
    int LineNumber,
    int MedicineBatchId,
    decimal Quantity,
    ReturnDisposition Disposition,
    string? Notes = null);
