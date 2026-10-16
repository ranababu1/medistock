using MediStock.Application.Common;
using MediStock.Domain.Inventory;

namespace MediStock.Application.Inventory;

/// <summary>What one correction did, so the screen can confirm it in the operator's own terms.</summary>
public sealed record StockCorrectionResult(
    int MedicineBatchId,
    StockMovementType MovementType,
    decimal QuantityDelta,
    decimal QuantityAfter,
    DateOnly BusinessDate);

/// <summary>What quarantining one batch did, so the screen can confirm it in the operator's own terms.</summary>
public sealed record StockQuarantineResult(
    int MedicineBatchId,
    BatchStatus Status,
    string Reason,
    decimal QuantityHeld);

/// <summary>
/// Manual stock corrections: adjustments that reconcile the shelf with the ledger, quarantines that hold a
/// batch's stock out of sale pending disposal, and write-offs that dispose of that stock for good.
/// </summary>
/// <remarks>
/// Both operations are one transaction. The cached batch balance moves, the movement is appended to the
/// stock ledger, and the audit entry is written — or none of it happens. Every quantity change in this
/// application appends to the ledger in the same unit of work that changes the cache, which is what
/// keeps reconciliation (cached balance equals ledger sum) a single rule. See
/// <c>docs/decisions/0011-stock-corrections.md</c>.
/// </remarks>
public interface IInventoryOperations
{
    /// <summary>
    /// Applies a manual adjustment to one batch: an increase books stock nobody had recorded, a decrease
    /// removes stock the ledger still claimed. Requires a reason, which both the movement and the audit
    /// entry carry.
    /// </summary>
    Task<OperationResult<StockCorrectionResult>> AdjustAsync(
        StockAdjustmentDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Disposes of a batch's entire remaining quantity, blocking the batch with the reason on it.
    /// A recall disposal marks the batch recalled; an expiry or damage disposal marks it written off.
    /// </summary>
    Task<OperationResult<StockCorrectionResult>> WriteOffAsync(
        StockWriteOffDraft draft,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Blocks a batch from all movement and sale while its stock stays on the shelf: the recall notice has
    /// arrived, or the stock has been condemned, and the goods are awaiting disposal. Disposal is a
    /// separate, deliberate step (<see cref="WriteOffAsync"/>). Requires a reason, which the batch and the
    /// audit entry both carry.
    /// </summary>
    Task<OperationResult<StockQuarantineResult>> QuarantineAsync(
        StockQuarantineDraft draft,
        CancellationToken cancellationToken = default);
}