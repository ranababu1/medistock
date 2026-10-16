using MediStock.Application.Common;

namespace MediStock.Application.Sales;

/// <summary>
/// Writing side of returns and cancellations.
/// </summary>
/// <remarks>
/// <para>
/// A return is recorded against a completed invoice and never edits it: the line quantities that have
/// come back are tracked on the invoice, the refund is recorded on the return, and a restock moves the
/// batch balance through the stock ledger like every other change. The return, its lines, the refund
/// and any restock movements are written in one transaction, so a failed return leaves the shelf and the
/// till exactly as they were.
/// </para>
/// <para>
/// The catalog re-reads the invoice inside that transaction rather than trusting the screen. A quantity
/// that has already come back, or a batch the line never sold, is refused with a message the operator can
/// act on.
/// </para>
/// </remarks>
public interface ISaleReturnCatalog
{
    /// <summary>Records a customer return, refunds it and disposes of the goods.</summary>
    Task<OperationResult<SalesReturnDto>> RecordAsync(
        SalesReturnDraft draft,
        CancellationToken cancellationToken = default);

    Task<SalesReturnDto?> FindAsync(int salesReturnId, CancellationToken cancellationToken = default);

    Task<SalesReturnDto?> FindByReturnNumberAsync(
        string returnNumber,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SalesReturnDto>> SearchAsync(
        SalesReturnQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Issues and returns the return number the next return would receive, without consuming it.</summary>
    Task<string> PreviewReturnNumberAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Cancels an invoice, once everything on it has been returned. Cancelling is a status change on a
    /// document that keeps its numbers: the stock the cancellation undoes is put back by the return, not
    /// by erasing the sale.
    /// </summary>
    Task<OperationResult<SaleDto>> CancelSaleAsync(
        int saleId,
        string cancelledBy,
        string reason,
        CancellationToken cancellationToken = default);
}
