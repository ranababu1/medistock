using MediStock.Application.Common;

namespace MediStock.Application.Suppliers;

/// <summary>Criteria for the supplier search box.</summary>
public sealed record SupplierSearchQuery
{
    public const int MaxResults = 500;

    /// <summary>Free text matched against name, contact person, phone, GSTIN and drug licence.</summary>
    public string? Term { get; init; }

    /// <summary>Inactive records are hidden by default but remain available for history.</summary>
    public bool IncludeInactive { get; init; }

    public int Take { get; init; } = 200;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Term) && !IncludeInactive;
}

/// <summary>
/// Supplier operations used by the desktop UI. Implemented in the infrastructure layer over
/// Entity Framework Core.
/// </summary>
public interface ISupplierCatalog
{
    Task<IReadOnlyList<SupplierDto>> SearchAsync(
        SupplierSearchQuery query,
        CancellationToken cancellationToken = default);

    Task<SupplierDto?> FindAsync(int supplierId, CancellationToken cancellationToken = default);

    Task<OperationResult<SupplierDto>> CreateAsync(
        SupplierDraft draft,
        CancellationToken cancellationToken = default);

    Task<OperationResult<SupplierDto>> UpdateAsync(
        int supplierId,
        SupplierDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>Soft activation toggle. Records are never deleted once stock exists.</summary>
    Task<OperationResult> SetActiveAsync(
        int supplierId,
        bool isActive,
        CancellationToken cancellationToken = default);

    /// <summary>Suppliers with at least one purchase, so the UI can warn before deactivating.</summary>
    Task<int> CountAsync(bool includeInactive, CancellationToken cancellationToken = default);
}
