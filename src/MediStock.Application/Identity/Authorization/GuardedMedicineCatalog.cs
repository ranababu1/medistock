using MediStock.Application.Catalogue;
using MediStock.Application.Common;
using MediStock.Domain.Identity;

namespace MediStock.Application.Identity.Authorization;

/// <summary>
/// Puts the medicine catalogue behind the permissions it needs.
/// </summary>
/// <remarks>
/// <para>
/// The check sits here, in the application layer, rather than in the medicine editor's button state,
/// because a hidden button is a convenience and not a rule: the same catalogue is reachable from a
/// report, an import or a future screen. Looking a medicine up stays open, because a cashier has to be
/// able to price a basket.
/// </para>
/// <para>
/// Editing a tax rate is checked twice over. The edit needs
/// <see cref="Permission.ManageMedicines"/>, and a change to <see cref="MedicineDto.TaxRatePercent"/>
/// additionally needs <see cref="Permission.ChangePrices"/>, so a store-room operator can correct a
/// misspelt brand without being able to move what the customer pays.
/// </para>
/// </remarks>
internal sealed class GuardedMedicineCatalog : IMedicineCatalog
{
    private readonly IMedicineCatalog _inner;
    private readonly IUserSession _session;

    public GuardedMedicineCatalog(IMedicineCatalog inner, IUserSession session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task<IReadOnlyList<MedicineDto>> SearchAsync(MedicineSearchQuery query, CancellationToken cancellationToken = default)
        => _inner.SearchAsync(query, cancellationToken);

    public Task<MedicineDto?> FindAsync(int medicineId, CancellationToken cancellationToken = default)
        => _inner.FindAsync(medicineId, cancellationToken);

    public Task<IReadOnlyList<MedicineDto>> FindPotentialDuplicatesAsync(MedicineDraft draft, int? excludeMedicineId = null, CancellationToken cancellationToken = default)
        => _inner.FindPotentialDuplicatesAsync(draft, excludeMedicineId, cancellationToken);

    public Task<int> CountAsync(bool includeInactive, CancellationToken cancellationToken = default)
        => _inner.CountAsync(includeInactive, cancellationToken);

    public Task<OperationResult<MedicineDto>> CreateAsync(MedicineDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        _session.Require(Permission.ManageMedicines, "add a medicine to the catalogue");

        return _inner.CreateAsync(draft, cancellationToken);
    }

    public async Task<OperationResult<MedicineDto>> UpdateAsync(int medicineId, MedicineDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        _session.Require(Permission.ManageMedicines, "edit this medicine");

        var current = await _inner.FindAsync(medicineId, cancellationToken).ConfigureAwait(false);
        if (current is not null && current.TaxRatePercent != draft.TaxRatePercent)
        {
            _session.Require(Permission.ChangePrices, "change the tax rate on this medicine");
        }

        return await _inner.UpdateAsync(medicineId, draft, cancellationToken).ConfigureAwait(false);
    }

    public Task<OperationResult> SetActiveAsync(int medicineId, bool isActive, CancellationToken cancellationToken = default)
    {
        _session.Require(
            Permission.ManageMedicines,
            isActive ? "put this medicine back into the catalogue" : "withdraw this medicine");

        return _inner.SetActiveAsync(medicineId, isActive, cancellationToken);
    }
}
