using MediStock.Application.Common;
using MediStock.Application.Suppliers;
using MediStock.Domain.Identity;

namespace MediStock.Application.Identity.Authorization;

/// <summary>
/// Puts the supplier list behind <see cref="Permission.ManageSuppliers"/>. Reading stays open, because
/// the purchase screen and the receiving screen both need to name a supplier to do their work.
/// </summary>
internal sealed class GuardedSupplierCatalog : ISupplierCatalog
{
    private readonly ISupplierCatalog _inner;
    private readonly IUserSession _session;

    public GuardedSupplierCatalog(ISupplierCatalog inner, IUserSession session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task<IReadOnlyList<SupplierDto>> SearchAsync(SupplierSearchQuery query, CancellationToken cancellationToken = default)
        => _inner.SearchAsync(query, cancellationToken);

    public Task<SupplierDto?> FindAsync(int supplierId, CancellationToken cancellationToken = default)
        => _inner.FindAsync(supplierId, cancellationToken);

    public Task<int> CountAsync(bool includeInactive, CancellationToken cancellationToken = default)
        => _inner.CountAsync(includeInactive, cancellationToken);

    public Task<OperationResult<SupplierDto>> CreateAsync(SupplierDraft draft, CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.ManageSuppliers, "add a supplier");
        return _inner.CreateAsync(draft, cancellationToken);
    }

    public Task<OperationResult<SupplierDto>> UpdateAsync(int supplierId, SupplierDraft draft, CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.ManageSuppliers, "edit this supplier");
        return _inner.UpdateAsync(supplierId, draft, cancellationToken);
    }

    public Task<OperationResult> SetActiveAsync(int supplierId, bool isActive, CancellationToken cancellationToken = default)
    {
        _session.Require(
            Permission.ManageSuppliers,
            isActive ? "put this supplier back into use" : "stop using this supplier");

        return _inner.SetActiveAsync(supplierId, isActive, cancellationToken);
    }
}
