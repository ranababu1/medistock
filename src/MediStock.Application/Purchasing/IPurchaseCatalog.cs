using MediStock.Application.Common;
using MediStock.Domain.Purchasing;

namespace MediStock.Application.Purchasing;

/// <summary>Criteria for the purchase list.</summary>
public sealed record PurchaseSearchQuery
{
    public const int MaxResults = 500;

    /// <summary>Free text matched against invoice number and supplier name.</summary>
    public string? Term { get; init; }

    public int? SupplierId { get; init; }

    public PurchaseStatus? Status { get; init; }

    public DateOnly? InvoiceDateFrom { get; init; }

    public DateOnly? InvoiceDateTo { get; init; }

    public int Take { get; init; } = 200;
}

/// <summary>
/// Purchasing and receiving operations used by the desktop UI. Receiving is the only way stock enters
/// the pharmacy, so every method that changes stock runs inside a single database transaction.
/// </summary>
public interface IPurchaseCatalog
{
    Task<IReadOnlyList<PurchaseSummaryDto>> SearchAsync(
        PurchaseSearchQuery query,
        CancellationToken cancellationToken = default);

    /// <summary>Loads a purchase with its lines.</summary>
    Task<PurchaseDto?> FindAsync(int purchaseId, CancellationToken cancellationToken = default);

    Task<OperationResult<PurchaseDto>> CreateAsync(
        PurchaseDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>Replaces the header and every line of a draft purchase.</summary>
    Task<OperationResult<PurchaseDto>> UpdateAsync(
        int purchaseId,
        PurchaseDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Confirms the purchase and takes the goods into stock: one batch and one receipt movement per
    /// line, in a single transaction. A purchase that is already received is refused, so a repeated
    /// submission cannot receive the same invoice twice.
    /// </summary>
    Task<OperationResult<PurchaseDto>> ReceiveAsync(
        int purchaseId,
        DateOnly receivedOn,
        string receivedBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Undoes a receipt. Refused once any of the batch stock has been sold or adjusted, so the
    /// correction can never quietly erase stock that has already left the shelf.
    /// </summary>
    Task<OperationResult<PurchaseDto>> ReverseAsync(
        int purchaseId,
        string reason,
        string reversedBy,
        CancellationToken cancellationToken = default);

    /// <summary>Abandons a draft purchase that never reached stock.</summary>
    Task<OperationResult<PurchaseDto>> CancelAsync(
        int purchaseId,
        string reason,
        string cancelledBy,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Purchases already recorded for this supplier and invoice number. Used to warn about a likely
    /// duplicate before the operator receives the same invoice twice. Never blocks the save.
    /// </summary>
    Task<IReadOnlyList<PurchaseSummaryDto>> FindByInvoiceAsync(
        int supplierId,
        string invoiceNumber,
        int? excludePurchaseId = null,
        CancellationToken cancellationToken = default);
}
