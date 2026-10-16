using MediStock.Application.Common;
using MediStock.Application.Suppliers;
using MediStock.Domain.Identity;

namespace MediStock.Application.Identity.Authorization;

/// <summary>
/// Guards supplier returns with <see cref="Permission.ReturnToSupplier"/>.
/// </summary>
/// <remarks>
/// Only the recording is gated: it is the operation that takes stock off the shelf and books a credit
/// against a supplier's account. Reading past returns stays open so an operator can answer a question
/// about a credit while a customer or supplier waits.
/// </remarks>
internal sealed class GuardedSupplierReturnCatalog : ISupplierReturnCatalog
{
    private readonly ISupplierReturnCatalog _inner;
    private readonly IUserSession _session;

    public GuardedSupplierReturnCatalog(ISupplierReturnCatalog inner, IUserSession session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task<OperationResult<SupplierReturnDto>> RecordAsync(
        SupplierReturnDraft draft,
        CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.ReturnToSupplier, "send this stock back to its supplier");
        return _inner.RecordAsync(draft, cancellationToken);
    }

    public Task<SupplierReturnDto?> FindAsync(int supplierReturnId, CancellationToken cancellationToken = default)
        => _inner.FindAsync(supplierReturnId, cancellationToken);

    public Task<IReadOnlyList<SupplierReturnSummaryDto>> SearchAsync(
        SupplierReturnQuery query,
        CancellationToken cancellationToken = default)
        => _inner.SearchAsync(query, cancellationToken);

    public Task<string> PreviewReturnNumberAsync(CancellationToken cancellationToken = default)
        => _inner.PreviewReturnNumberAsync(cancellationToken);
}