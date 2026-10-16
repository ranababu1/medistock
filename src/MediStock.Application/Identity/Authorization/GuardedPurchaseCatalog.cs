using MediStock.Application.Common;
using MediStock.Application.Purchasing;
using MediStock.Domain.Identity;

namespace MediStock.Application.Identity.Authorization;

/// <summary>
/// Puts purchasing and receiving behind <see cref="Permission.ReceivePurchases"/>, with
/// <see cref="Permission.ChangePrices"/> needed as well to move a line's selling price.
/// </summary>
/// <remarks>
/// Everything that changes stock is gated, including cancellation and reversal, because a reversal takes
/// goods back out of the shelf. Reading the purchase list is not: the inventory screen and the reports
/// both need it.
/// </remarks>
internal sealed class GuardedPurchaseCatalog : IPurchaseCatalog
{
    private readonly IPurchaseCatalog _inner;
    private readonly IUserSession _session;

    public GuardedPurchaseCatalog(IPurchaseCatalog inner, IUserSession session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task<IReadOnlyList<PurchaseSummaryDto>> SearchAsync(PurchaseSearchQuery query, CancellationToken cancellationToken = default)
        => _inner.SearchAsync(query, cancellationToken);

    public Task<PurchaseDto?> FindAsync(int purchaseId, CancellationToken cancellationToken = default)
        => _inner.FindAsync(purchaseId, cancellationToken);

    public Task<IReadOnlyList<PurchaseSummaryDto>> FindByInvoiceAsync(int supplierId, string invoiceNumber, int? excludePurchaseId = null, CancellationToken cancellationToken = default)
        => _inner.FindByInvoiceAsync(supplierId, invoiceNumber, excludePurchaseId, cancellationToken);

    public Task<OperationResult<PurchaseDto>> CreateAsync(PurchaseDraft draft, CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.ReceivePurchases, "record a supplier invoice");
        return _inner.CreateAsync(draft, cancellationToken);
    }

    public async Task<OperationResult<PurchaseDto>> UpdateAsync(int purchaseId, PurchaseDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        _session.Require(Permission.ReceivePurchases, "edit this supplier invoice");

        var current = await _inner.FindAsync(purchaseId, cancellationToken).ConfigureAwait(false);
        if (current is not null && PricesMoved(current, draft))
        {
            _session.Require(Permission.ChangePrices, "change a selling price on this invoice");
        }

        return await _inner.UpdateAsync(purchaseId, draft, cancellationToken).ConfigureAwait(false);
    }

    public Task<OperationResult<PurchaseDto>> ReceiveAsync(int purchaseId, DateOnly receivedOn, string receivedBy, CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.ReceivePurchases, "receive these goods into stock");
        return _inner.ReceiveAsync(purchaseId, receivedOn, receivedBy, cancellationToken);
    }

    public Task<OperationResult<PurchaseDto>> ReverseAsync(int purchaseId, string reason, string reversedBy, CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.ReceivePurchases, "reverse this receipt");
        return _inner.ReverseAsync(purchaseId, reason, reversedBy, cancellationToken);
    }

    public Task<OperationResult<PurchaseDto>> CancelAsync(int purchaseId, string reason, string cancelledBy, CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.ReceivePurchases, "cancel this supplier invoice");
        return _inner.CancelAsync(purchaseId, reason, cancelledBy, cancellationToken);
    }

    /// <summary>
    /// True when the draft prices a line that already exists differently from what is stored. Any
    /// difference counts, up or down, because the pharmacy cares about both equally.
    /// </summary>
    /// <remarks>
    /// A line the invoice does not have yet is not a price change: it is part of recording the invoice, and
    /// the receiving permission covers it. What needs the extra permission is quietly re-pricing something
    /// that has already been written down.
    /// </remarks>
    private static bool PricesMoved(PurchaseDto current, PurchaseDraft draft)
    {
        var stored = current.Lines.ToDictionary(Key, l => l.SellingPrice);

        foreach (var line in draft.Lines)
        {
            var key = (line.MedicineId, line.BatchNumber.Trim().ToUpperInvariant(), line.ExpiryDate);
            if (stored.TryGetValue(key, out var existing) && existing != line.SellingPrice)
            {
                return true;
            }
        }

        return false;
    }

    private static (int MedicineId, string BatchNumber, DateOnly ExpiryDate) Key(PurchaseLineDto line)
        => (line.MedicineId, line.BatchNumber.Trim().ToUpperInvariant(), line.ExpiryDate);
}
