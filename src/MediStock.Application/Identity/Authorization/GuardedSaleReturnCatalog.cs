using MediStock.Application.Common;
using MediStock.Application.Sales;
using MediStock.Domain.Identity;

namespace MediStock.Application.Identity.Authorization;

/// <summary>
/// Guards returns and cancellations with <see cref="Permission.ProcessReturns"/>.
/// </summary>
/// <remarks>
/// Both writing operations are gated, cancellation included: cancelling an invoice refunds money back out of
/// the till and is not something a cashier can do on their own authority. Reading a past return stays open
/// so a cashier can answer a customer's question about it.
/// </remarks>
internal sealed class GuardedSaleReturnCatalog : ISaleReturnCatalog
{
    private readonly ISaleReturnCatalog _inner;
    private readonly IUserSession _session;

    public GuardedSaleReturnCatalog(ISaleReturnCatalog inner, IUserSession session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task<OperationResult<SalesReturnDto>> RecordAsync(SalesReturnDraft draft, CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.ProcessReturns, "record this return and refund it");
        return _inner.RecordAsync(draft, cancellationToken);
    }

    public Task<OperationResult<SaleDto>> CancelSaleAsync(int saleId, string cancelledBy, string reason, CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.ProcessReturns, "cancel this invoice");
        return _inner.CancelSaleAsync(saleId, cancelledBy, reason, cancellationToken);
    }

    public Task<SalesReturnDto?> FindAsync(int salesReturnId, CancellationToken cancellationToken = default)
        => _inner.FindAsync(salesReturnId, cancellationToken);

    public Task<SalesReturnDto?> FindByReturnNumberAsync(string returnNumber, CancellationToken cancellationToken = default)
        => _inner.FindByReturnNumberAsync(returnNumber, cancellationToken);

    public Task<IReadOnlyList<SalesReturnDto>> SearchAsync(SalesReturnQuery query, CancellationToken cancellationToken = default)
        => _inner.SearchAsync(query, cancellationToken);

    public Task<string> PreviewReturnNumberAsync(CancellationToken cancellationToken = default)
        => _inner.PreviewReturnNumberAsync(cancellationToken);
}
