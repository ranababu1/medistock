using MediStock.Application.Common;

namespace MediStock.Application.Catalogue;

/// <summary>Criteria for the catalogue search box.</summary>
public sealed record MedicineSearchQuery
{
    public const int MaxResults = 500;

    /// <summary>Free text matched against name, brand, generic name, manufacturer, and barcode.</summary>
    public string? Term { get; init; }

    /// <summary>Inactive records are hidden by default but remain available for history.</summary>
    public bool IncludeInactive { get; init; }

    public int Take { get; init; } = 200;

    public bool IsEmpty => string.IsNullOrWhiteSpace(Term) && !IncludeInactive;
}

/// <summary>
/// Catalogue operations used by the desktop UI. Implemented in the infrastructure layer over
/// Entity Framework Core.
/// </summary>
public interface IMedicineCatalog
{
    Task<IReadOnlyList<MedicineDto>> SearchAsync(
        MedicineSearchQuery query,
        CancellationToken cancellationToken = default);

    Task<MedicineDto?> FindAsync(int medicineId, CancellationToken cancellationToken = default);

    Task<OperationResult<MedicineDto>> CreateAsync(
        MedicineDraft draft,
        CancellationToken cancellationToken = default);

    Task<OperationResult<MedicineDto>> UpdateAsync(
        int medicineId,
        MedicineDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>Soft activation toggle. Records are never deleted once stock exists.</summary>
    Task<OperationResult> SetActiveAsync(
        int medicineId,
        bool isActive,
        CancellationToken cancellationToken = default);

    /// <summary>Finds near-duplicates so the operator can confirm before adding a second entry.</summary>
    Task<IReadOnlyList<MedicineDto>> FindPotentialDuplicatesAsync(
        MedicineDraft draft,
        int? excludeMedicineId = null,
        CancellationToken cancellationToken = default);

    Task<int> CountAsync(bool includeInactive, CancellationToken cancellationToken = default);
}
