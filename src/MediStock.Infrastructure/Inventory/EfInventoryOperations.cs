using System.Globalization;
using MediStock.Application.Common;
using MediStock.Application.Identity;
using MediStock.Application.Inventory;
using MediStock.Domain.Common;
using MediStock.Domain.Identity;
using MediStock.Domain.Inventory;
using MediStock.Infrastructure.Identity;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Inventory;

/// <summary>
/// Entity Framework Core implementation of manual stock corrections.
/// </summary>
/// <remarks>
/// <para>
/// A correction is one transaction. The batch is re-read inside it, the cached balance moves through the
/// domain's own guard, one movement is appended to the stock ledger, and the audit entry is written - or
/// none of it happens. A correction that fails leaves the ledger exactly as it was.
/// </para>
/// <para>
/// The screen's copy of the batch is never trusted. Two operators can have the same batch open, so the
/// balance is re-read here and the write is conditional: the batch carries a rowversion, so the operator
/// who loses that race matches no row, the transaction rolls back, and the whole unit of work is repeated
/// against the balance the batch really holds.
/// </para>
/// <para>
/// See <c>docs/decisions/0011-stock-corrections.md</c>.
/// </para>
/// </remarks>
internal sealed class EfInventoryOperations : IInventoryOperations
{
    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IClock _clock;
    private readonly IUserSession _session;
    private readonly ILogger<EfInventoryOperations> _logger;

    public EfInventoryOperations(
        IDbContextFactory<MediStockDbContext> factory,
        IClock clock,
        IUserSession session,
        ILogger<EfInventoryOperations> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OperationResult<StockCorrectionResult>> AdjustAsync(
        StockAdjustmentDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var request = draft.Normalise();

        if (request.MedicineBatchId <= 0)
        {
            return OperationResult<StockCorrectionResult>.Fail(
                "Choose the batch whose count is being corrected first.");
        }

        if (request.Quantity <= 0m)
        {
            return OperationResult<StockCorrectionResult>.Fail(
                "Enter a quantity greater than zero to adjust the count by.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return OperationResult<StockCorrectionResult>.Fail(
                "A reason is required, because the adjustment is what the audit trail will show.");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var outcome = await UnitOfWorkRetry.ExecuteAsync(
            db,
            async () =>
            {
                // See the class remarks: a retry repeats the whole unit of work, never part of one.
                db.ChangeTracker.Clear();

                var businessDate = request.BusinessDate ?? _clock.BusinessDate;
                var now = _clock.UtcNow;

                await using var transaction = await db.Database
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    var batch = await db.MedicineBatches
                        .FirstOrDefaultAsync(candidate => candidate.Id == request.MedicineBatchId, cancellationToken)
                        .ConfigureAwait(false);

                    if (batch is null)
                    {
                        return OperationResult<StockCorrectionResult>.Fail(
                            "That batch could not be found, so nothing was adjusted. Reload the inventory list "
                            + "and choose the batch again.");
                    }

                    var movementType = request.IsIncrease
                        ? StockMovementType.AdjustmentIncrease
                        : StockMovementType.AdjustmentDecrease;
                    var delta = request.IsIncrease ? request.Quantity : -request.Quantity;

                    if (request.IsIncrease)
                    {
                        batch.IncreaseQuantity(request.Quantity, now);
                    }
                    else
                    {
                        batch.DecreaseQuantity(request.Quantity, now);
                    }

                    var batchId = batch.Id;

                    db.StockMovements.Add(StockMovement.Record(
                        batch.MedicineId,
                        batchId,
                        movementType,
                        delta,
                        batch.QuantityOnHand,
                        businessDate,
                        Operator(request.AdjustedBy),
                        reason: request.Reason,
                        nowUtc: now));

                    var (actor, actorId) = OperatorOf(request.AdjustedBy);

                    AuditJournal.Stage(
                        db,
                        new AuditRecord(
                            AuditAction.StockAdjusted,
                            $"Batch '{batch.BatchNumber}' was corrected: {request.Quantity:0.###} unit(s) "
                            + (request.IsIncrease ? "added." : "removed."))
                        {
                            EntityType = "MedicineBatch",
                            EntityId = batch.BatchNumber,
                            Reason = request.Reason,
                            Detail = request.Notes,
                        },
                        actor,
                        actorId);

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return OperationResult<StockCorrectionResult>.Success(
                        new StockCorrectionResult(
                            batchId,
                            movementType,
                            delta,
                            batch.QuantityOnHand,
                            businessDate));
                }
                catch (DomainException ex)
                {
                    // Everything the operator can put right: more stock removed than the batch holds, a
                    // batch blocked from being moved.
                    return OperationResult<StockCorrectionResult>.Fail(ex.Message);
                }
                catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
                {
                    return OperationResult<StockCorrectionResult>.Fail(message);
                }
            },
            "Another operator was changing this batch at the same moment, so the adjustment was not saved and "
            + "the count was not changed. Reload the batch and try again.",
            _logger,
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            _logger.LogInformation(
                "Refused to adjust batch {MedicineBatchId} by {Quantity:0.###} unit(s): {Reasons}",
                request.MedicineBatchId,
                request.IsIncrease ? request.Quantity : -request.Quantity,
                string.Join(" | ", outcome.Errors));

            return OperationResult<StockCorrectionResult>.Fail(outcome.Errors);
        }

        var result = outcome.Value
            ?? throw new InvalidOperationException("An adjustment that succeeded produced no result.");

        _logger.LogInformation(
            "Adjusted batch {MedicineBatchId} by {Delta:0.###} unit(s); it now holds {OnHand:0.###}.",
            result.MedicineBatchId,
            result.QuantityDelta,
            result.QuantityAfter);

        return OperationResult<StockCorrectionResult>.Success(result);
    }

    public async Task<OperationResult<StockQuarantineResult>> QuarantineAsync(
        StockQuarantineDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var request = draft.Normalise();

        if (request.MedicineBatchId <= 0)
        {
            return OperationResult<StockQuarantineResult>.Fail(
                "Choose the batch whose stock is being held back first.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return OperationResult<StockQuarantineResult>.Fail(
                "A reason is required - the recall notice, the condemnation or the damage report this "
                + "hold is acting on.");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var outcome = await UnitOfWorkRetry.ExecuteAsync(
            db,
            async () =>
            {
                // See the class remarks: a retry repeats the whole unit of work, never part of one.
                db.ChangeTracker.Clear();

                var now = _clock.UtcNow;

                await using var transaction = await db.Database
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    var batch = await db.MedicineBatches
                        .FirstOrDefaultAsync(candidate => candidate.Id == request.MedicineBatchId, cancellationToken)
                        .ConfigureAwait(false);

                    if (batch is null)
                    {
                        return OperationResult<StockQuarantineResult>.Fail(
                            "That batch could not be found, so nothing was quarantined. Reload the inventory "
                            + "list and choose the batch again.");
                    }

                    var status = request.IsRecall ? BatchStatus.Recalled : BatchStatus.WrittenOff;

                    // The hold, not the disposal: the quantity stays on the shelf and on the ledger, and
                    // nothing moves until the batch is written off.
                    batch.WriteOff(request.Reason, status, now);

                    var (actor, actorId) = OperatorOf(request.QuarantinedBy);

                    AuditJournal.Stage(
                        db,
                        new AuditRecord(
                            AuditAction.StockQuarantined,
                            $"Batch '{batch.BatchNumber}' was {(request.IsRecall ? "recalled" : "blocked")}: "
                            + $"{batch.QuantityOnHand:0.###} unit(s) held out of sale.")
                        {
                            EntityType = "MedicineBatch",
                            EntityId = batch.BatchNumber,
                            Reason = request.Reason,
                            Detail = request.Notes,
                        },
                        actor,
                        actorId);

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return OperationResult<StockQuarantineResult>.Success(
                        new StockQuarantineResult(batch.Id, status, request.Reason, batch.QuantityOnHand));
                }
                catch (DomainException ex)
                {
                    // Everything the operator can put right: a batch with nothing left to hold back.
                    return OperationResult<StockQuarantineResult>.Fail(ex.Message);
                }
                catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
                {
                    return OperationResult<StockQuarantineResult>.Fail(message);
                }
            },
            "Another operator was changing this batch at the same moment, so it was not quarantined. Reload "
            + "the batch and try again.",
            _logger,
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            _logger.LogInformation(
                "Refused to quarantine batch {MedicineBatchId}: {Reasons}",
                request.MedicineBatchId,
                string.Join(" | ", outcome.Errors));

            return OperationResult<StockQuarantineResult>.Fail(outcome.Errors);
        }

        var quarantined = outcome.Value
            ?? throw new InvalidOperationException("A quarantine that succeeded produced no result.");

        _logger.LogInformation(
            "Quarantined batch {MedicineBatchId}: {Quantity:0.###} unit(s) held out of sale as {Status}.",
            quarantined.MedicineBatchId,
            quarantined.QuantityHeld,
            quarantined.Status);

        return OperationResult<StockQuarantineResult>.Success(quarantined);
    }

    public async Task<OperationResult<StockCorrectionResult>> WriteOffAsync(
        StockWriteOffDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var request = draft.Normalise();

        if (request.MedicineBatchId <= 0)
        {
            return OperationResult<StockCorrectionResult>.Fail(
                "Choose the batch whose stock is being written off first.");
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            return OperationResult<StockCorrectionResult>.Fail(
                "A reason is required - the recall notice, the expiry decision or the damage report this "
                + "write-off is acting on.");
        }

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var outcome = await UnitOfWorkRetry.ExecuteAsync(
            db,
            async () =>
            {
                // See the class remarks: a retry repeats the whole unit of work, never part of one.
                db.ChangeTracker.Clear();

                var businessDate = request.BusinessDate ?? _clock.BusinessDate;
                var now = _clock.UtcNow;

                await using var transaction = await db.Database
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    var batch = await db.MedicineBatches
                        .FirstOrDefaultAsync(candidate => candidate.Id == request.MedicineBatchId, cancellationToken)
                        .ConfigureAwait(false);

                    if (batch is null)
                    {
                        return OperationResult<StockCorrectionResult>.Fail(
                            "That batch could not be found, so nothing was written off. Reload the inventory "
                            + "list and choose the batch again.");
                    }

                    var status = request.IsRecall ? BatchStatus.Recalled : BatchStatus.WrittenOff;
                    var movementType = request.IsRecall
                        ? StockMovementType.RecallWriteOff
                        : StockMovementType.ExpiryWriteOff;

                    var batchId = batch.Id;
                    var disposed = batch.WriteOffRemainingStock(request.Reason, status, now);

                    db.StockMovements.Add(StockMovement.Record(
                        batch.MedicineId,
                        batchId,
                        movementType,
                        -disposed,
                        batch.QuantityOnHand,
                        businessDate,
                        Operator(request.WrittenOffBy),
                        reason: request.Reason,
                        nowUtc: now));

                    var (actor, actorId) = OperatorOf(request.WrittenOffBy);

                    AuditJournal.Stage(
                        db,
                        new AuditRecord(
                            AuditAction.StockWrittenOff,
                            $"Batch '{batch.BatchNumber}' was "
                            + (request.IsRecall ? "recalled" : "written off")
                            + $": {disposed:0.###} unit(s) disposed of.")
                        {
                            EntityType = "MedicineBatch",
                            EntityId = batch.BatchNumber,
                            Reason = request.Reason,
                            Detail = request.Notes,
                        },
                        actor,
                        actorId);

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return OperationResult<StockCorrectionResult>.Success(
                        new StockCorrectionResult(
                            batchId,
                            movementType,
                            -disposed,
                            batch.QuantityOnHand,
                            businessDate));
                }
                catch (DomainException ex)
                {
                    // Everything the operator can put right: a batch with nothing left to write off.
                    return OperationResult<StockCorrectionResult>.Fail(ex.Message);
                }
                catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
                {
                    return OperationResult<StockCorrectionResult>.Fail(message);
                }
            },
            "Another operator was changing this batch at the same moment, so nothing was written off. Reload "
            + "the batch and try again.",
            _logger,
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            _logger.LogInformation(
                "Refused to write off batch {MedicineBatchId}: {Reasons}",
                request.MedicineBatchId,
                string.Join(" | ", outcome.Errors));

            return OperationResult<StockCorrectionResult>.Fail(outcome.Errors);
        }

        var writeOff = outcome.Value
            ?? throw new InvalidOperationException("A write-off that succeeded produced no result.");

        _logger.LogInformation(
            "Wrote off batch {MedicineBatchId}: {Quantity:0.###} unit(s) disposed of.",
            writeOff.MedicineBatchId,
            -writeOff.QuantityDelta);

        return OperationResult<StockCorrectionResult>.Success(writeOff);
    }

    /// <summary>
    /// The name a stock movement is signed with. A correction is made by the signed-in operator on the
    /// screen, so an empty box falls back to the account's display name.
    /// </summary>
    private string Operator(string? operatorName)
    {
        var name = Normalise(operatorName);

        return name ?? AuditJournal.ActorOf(_session).Actor;
    }

    /// <summary>
    /// The account a correction is linked to in the audit trail, when the name it is signed with really is
    /// the signed-in one. The operator box is free text, so the two can differ; the trail records what was
    /// typed and only links an account when it matches.
    /// </summary>
    private (string Actor, int? UserId) OperatorOf(string? operatorName)
    {
        var fallback = AuditJournal.ActorOf(_session);

        if (string.IsNullOrWhiteSpace(operatorName))
        {
            return fallback;
        }

        var name = operatorName.Trim();
        var current = _session.CurrentUser;

        return (name, current is not null && string.Equals(current.UserName, name, StringComparison.OrdinalIgnoreCase)
            ? current.Id
            : null);
    }

    private static string? Normalise(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static bool TryDescribeConstraintViolation(DbUpdateException exception, out string message)
    {
        message = string.Empty;

        if (!SqlFailure.TryClassify(exception, out _, out _))
        {
            return false;
        }

        // The check constraints on the batch are the last line of defence: the domain refuses the
        // impossible first - a negative balance, a blank reason - and this is the message if a race got
        // past it.
        message = "The correction was rejected by a database rule, so nothing was changed and the ledger was "
            + "left exactly as it was. Reload the batch and try again.";

        return true;
    }
}