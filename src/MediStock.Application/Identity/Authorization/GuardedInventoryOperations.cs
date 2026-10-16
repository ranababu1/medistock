using MediStock.Application.Common;
using MediStock.Application.Inventory;
using MediStock.Domain.Identity;

namespace MediStock.Application.Identity.Authorization;

/// <summary>
/// Guards manual stock corrections with <see cref="Permission.AdjustStock"/>.
/// </summary>
/// <remarks>
/// All three corrections change what the ledger claims the shelf holds, so all need the same authority: an
/// adjustment reconciles the two, a quarantine holds stock out of sale pending disposal, and a write-off
/// disposes of stock for good. None is a stronger permission on its own because none is undoable — the
/// correction is the audit trail.
/// </remarks>
internal sealed class GuardedInventoryOperations : IInventoryOperations
{
    private readonly IInventoryOperations _inner;
    private readonly IUserSession _session;

    public GuardedInventoryOperations(IInventoryOperations inner, IUserSession session)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
        _session = session ?? throw new ArgumentNullException(nameof(session));
    }

    public Task<OperationResult<StockCorrectionResult>> AdjustAsync(
        StockAdjustmentDraft draft,
        CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.AdjustStock, "adjust this batch count");
        return _inner.AdjustAsync(draft, cancellationToken);
    }

    public Task<OperationResult<StockCorrectionResult>> WriteOffAsync(
        StockWriteOffDraft draft,
        CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.AdjustStock, "write off this batch");
        return _inner.WriteOffAsync(draft, cancellationToken);
    }

    public Task<OperationResult<StockQuarantineResult>> QuarantineAsync(
        StockQuarantineDraft draft,
        CancellationToken cancellationToken = default)
    {
        _session.Require(Permission.AdjustStock, "quarantine this batch");
        return _inner.QuarantineAsync(draft, cancellationToken);
    }
}