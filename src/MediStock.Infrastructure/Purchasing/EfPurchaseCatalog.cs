using System.Linq.Expressions;
using MediStock.Application.Common;
using MediStock.Application.Purchasing;
using MediStock.Domain.Common;
using MediStock.Domain.Inventory;
using MediStock.Domain.Purchasing;
using MediStock.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Purchasing;

/// <summary>
/// Entity Framework Core implementation of purchasing and receiving. Receiving is the only path by
/// which stock enters the pharmacy, so it always runs inside a single database transaction: a partial
/// receipt cannot survive a failure.
/// </summary>
/// <remarks>
/// The connection is configured to retry transient faults, and EF Core refuses to run statements
/// inside a user-initiated transaction unless the whole unit of work is handed to its execution
/// strategy. Every mutating operation that opens a transaction therefore runs through
/// <see cref="ExecuteUnitOfWorkAsync"/>, starts each attempt with a clean change tracker, and returns
/// only the identifier of the affected purchase. Reloading and logging happen after the strategy, so a
/// retry can never repeat either.
/// <para>
/// The purchase header carries a database rowversion, so a lifecycle transition is a conditional write:
/// an operation that read the header before another operation changed it is refused instead of
/// overwriting the newer state. Two operators receiving the same delivery at the same moment therefore
/// take it into stock once, not twice. See
/// <c>docs/decisions/0006-context-lifetime-and-execution-strategy.md</c>.
/// </para>
/// </remarks>
internal sealed class EfPurchaseCatalog : IPurchaseCatalog
{
    /// <summary>
    /// Header projection for list screens. Deliberately omits the total, which is summed from the line
    /// rows so the figure always equals the sum of the rounded line values shown to the operator.
    /// </summary>
    private static readonly Expression<Func<Purchase, PurchaseSummaryDto>> SummaryProjection = p => new PurchaseSummaryDto
    {
        Id = p.Id,
        SupplierId = p.SupplierId,
        SupplierName = p.Supplier != null ? p.Supplier.Name : string.Empty,
        InvoiceNumber = p.InvoiceNumber,
        InvoiceDate = p.InvoiceDate,
        Status = p.Status,
        LineCount = p.Lines.Count,
        ReceivedOn = p.ReceivedOn,
        CreatedAtUtc = p.CreatedAtUtc,
    };

    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IClock _clock;
    private readonly ILogger<EfPurchaseCatalog> _logger;

    public EfPurchaseCatalog(
        IDbContextFactory<MediStockDbContext> factory,
        IClock clock,
        ILogger<EfPurchaseCatalog> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<IReadOnlyList<PurchaseSummaryDto>> SearchAsync(
        PurchaseSearchQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = db.Purchases.AsNoTracking();

        if (query.SupplierId is { } supplierId)
        {
            source = source.Where(p => p.SupplierId == supplierId);
        }

        if (query.Status is { } status)
        {
            source = source.Where(p => p.Status == status);
        }

        if (query.InvoiceDateFrom is { } from)
        {
            source = source.Where(p => p.InvoiceDate >= from);
        }

        if (query.InvoiceDateTo is { } to)
        {
            source = source.Where(p => p.InvoiceDate <= to);
        }

        if (Normalise(query.Term) is { } term)
        {
            var pattern = $"%{EscapeLike(term)}%";
            source = source.Where(p =>
                EF.Functions.Like(p.InvoiceNumber, pattern)
                || (p.Supplier != null && EF.Functions.Like(p.Supplier.Name, pattern)));
        }

        var take = Math.Clamp(query.Take, 1, PurchaseSearchQuery.MaxResults);

        var page = await source
            .OrderByDescending(p => p.InvoiceDate)
            .ThenByDescending(p => p.Id)
            .Take(take)
            .Select(SummaryProjection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return await WithTotalsAsync(db, page, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<PurchaseSummaryDto>> FindByInvoiceAsync(
        int supplierId,
        string invoiceNumber,
        int? excludePurchaseId = null,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var number = Normalise(invoiceNumber);
        if (number is null)
        {
            return [];
        }

        var source = db.Purchases.AsNoTracking().Where(p => p.SupplierId == supplierId && p.InvoiceNumber == number);

        if (excludePurchaseId is { } excluded)
        {
            source = source.Where(p => p.Id != excluded);
        }

        var page = await source
            .OrderByDescending(p => p.Id)
            .Take(10)
            .Select(SummaryProjection)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return await WithTotalsAsync(db, page, cancellationToken).ConfigureAwait(false);
    }

    public async Task<PurchaseDto?> FindAsync(int purchaseId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var purchase = await db.Purchases
            .AsNoTracking()
            .Include(p => p.Supplier)
            .Include(p => p.Lines)
                .ThenInclude(line => line.Medicine)
            .FirstOrDefaultAsync(p => p.Id == purchaseId, cancellationToken)
            .ConfigureAwait(false);

        return purchase is null ? null : Project(purchase);
    }

    public async Task<OperationResult<PurchaseDto>> CreateAsync(
        PurchaseDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        Purchase purchase;
        try
        {
            purchase = draft.ToNewPurchase(_clock.UtcNow);
        }
        catch (DomainValidationException ex)
        {
            _logger.LogInformation("Rejected purchase draft: {Reason}", ex.Message);
            return OperationResult<PurchaseDto>.Fail(ex.Message);
        }

        db.Purchases.Add(purchase);

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
        {
            db.Entry(purchase).State = EntityState.Detached;
            return OperationResult<PurchaseDto>.Fail(message);
        }

        _logger.LogInformation(
            "Created purchase {PurchaseId} for supplier {SupplierId}, invoice {InvoiceNumber}.",
            purchase.Id,
            purchase.SupplierId,
            purchase.InvoiceNumber);

        return OperationResult<PurchaseDto>.Success(await ReloadAsync(purchase.Id, cancellationToken).ConfigureAwait(false));
    }

    public async Task<OperationResult<PurchaseDto>> UpdateAsync(
        int purchaseId,
        PurchaseDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var outcome = await ExecuteUnitOfWorkAsync(db, purchaseId, async () =>
        {
            // See the class remarks: every attempt repeats the whole unit of work, never part of one.
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var purchase = await db.Purchases
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.Id == purchaseId, cancellationToken)
                .ConfigureAwait(false);

            if (purchase is null)
            {
                return OperationResult<int>.Fail($"Purchase {purchaseId} no longer exists.");
            }

            try
            {
                purchase.UpdateHeader(draft.SupplierId, draft.InvoiceNumber, draft.InvoiceDate, draft.Notes, _clock.UtcNow);

                foreach (var lineId in purchase.Lines.Select(line => line.Id).ToList())
                {
                    purchase.RemoveLine(lineId, _clock.UtcNow);
                }
            }
            catch (DomainValidationException ex)
            {
                return OperationResult<int>.Fail(ex.Message);
            }

            // Deletes are flushed before the replacements are added so that a re-entered medicine and batch
            // number cannot collide with the row still marked for deletion.
            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
            {
                return OperationResult<int>.Fail(message);
            }

            try
            {
                foreach (var line in draft.Lines)
                {
                    purchase.AddLine(
                        line.MedicineId,
                        line.BatchNumber,
                        line.ExpiryDate,
                        line.Quantity,
                        line.UnitCost,
                        line.SellingPrice,
                        _clock.UtcNow);
                }
            }
            catch (DomainValidationException ex)
            {
                return OperationResult<int>.Fail(ex.Message);
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
            {
                return OperationResult<int>.Fail(message);
            }

            return OperationResult<int>.Success(purchase.Id);
        }).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return OperationResult<PurchaseDto>.Fail(outcome.Errors);
        }

        _logger.LogInformation("Updated purchase {PurchaseId}.", outcome.Value);

        return OperationResult<PurchaseDto>.Success(await ReloadAsync(outcome.Value, cancellationToken).ConfigureAwait(false));
    }

    public async Task<OperationResult<PurchaseDto>> ReceiveAsync(
        int purchaseId,
        DateOnly receivedOn,
        string receivedBy,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var outcome = await ExecuteUnitOfWorkAsync(db, purchaseId, async () =>
        {
            // See the class remarks: every attempt repeats the whole unit of work, never part of one.
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var purchase = await db.Purchases
                .Include(p => p.Lines)
                .FirstOrDefaultAsync(p => p.Id == purchaseId, cancellationToken)
                .ConfigureAwait(false);

            if (purchase is null)
            {
                return OperationResult<int>.Fail($"Purchase {purchaseId} no longer exists.");
            }

            IReadOnlyList<PurchaseLineReceipt> receipts;
            try
            {
                receipts = purchase.Receive(receivedOn, receivedBy, _clock.UtcNow);
            }
            catch (DomainValidationException ex)
            {
                _logger.LogInformation("Refused to receive purchase {PurchaseId}: {Reason}", purchaseId, ex.Message);
                return OperationResult<int>.Fail(ex.Message);
            }

            var now = _clock.UtcNow;
            var batches = new List<MedicineBatch>(receipts.Count);

            try
            {
                foreach (var receipt in receipts)
                {
                    var batch = MedicineBatch.Receive(
                        receipt.MedicineId,
                        receipt.BatchNumber,
                        receipt.ExpiryDate,
                        receipt.Quantity,
                        receipt.UnitCost,
                        receipt.SellingPrice,
                        receivedOn,
                        purchase.SupplierId,
                        purchase.InvoiceNumber,
                        now);

                    db.MedicineBatches.Add(batch);
                    batches.Add(batch);
                }
            }
            catch (DomainValidationException ex)
            {
                return OperationResult<int>.Fail(ex.Message);
            }

            // Batches must be persisted first: a stock movement cannot reference a batch that has no key.
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

            var businessDate = _clock.BusinessDate;
            for (var index = 0; index < receipts.Count; index++)
            {
                var receipt = receipts[index];
                var batch = batches[index];

                purchase.AttachBatch(receipt.LineId, batch);

                db.StockMovements.Add(StockMovement.Record(
                    receipt.MedicineId,
                    batch.Id,
                    StockMovementType.Receipt,
                    receipt.Quantity,
                    batch.QuantityOnHand,
                    businessDate,
                    receivedBy,
                    reason: null,
                    reference: Reference(purchase.Id),
                    nowUtc: now));
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
            {
                return OperationResult<int>.Fail(message);
            }

            return OperationResult<int>.Success(purchase.Id);
        }).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return OperationResult<PurchaseDto>.Fail(outcome.Errors);
        }

        var reloaded = await ReloadAsync(outcome.Value, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Received purchase {PurchaseId} ({InvoiceNumber}): {LineCount} batch(es) added to stock.",
            reloaded.Id,
            reloaded.InvoiceNumber,
            reloaded.Lines.Count);

        return OperationResult<PurchaseDto>.Success(reloaded);
    }

    public async Task<OperationResult<PurchaseDto>> ReverseAsync(
        int purchaseId,
        string reason,
        string reversedBy,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var outcome = await ExecuteUnitOfWorkAsync(db, purchaseId, async () =>
        {
            // See the class remarks: every attempt repeats the whole unit of work, never part of one.
            db.ChangeTracker.Clear();

            await using var transaction = await db.Database.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

            var purchase = await db.Purchases
                .Include(p => p.Lines)
                    .ThenInclude(line => line.MedicineBatch)
                .FirstOrDefaultAsync(p => p.Id == purchaseId, cancellationToken)
                .ConfigureAwait(false);

            if (purchase is null)
            {
                return OperationResult<int>.Fail($"Purchase {purchaseId} no longer exists.");
            }

            if (!purchase.CanReverse)
            {
                return OperationResult<int>.Fail(
                    $"Only a received purchase can be reversed. This purchase is {purchase.Status}.");
            }

            var lines = purchase.Lines.Where(line => line.MedicineBatch is not null).ToList();
            if (lines.Count != purchase.Lines.Count)
            {
                return OperationResult<int>.Fail(
                    "This purchase is marked as received but one or more lines have no batch. " +
                    "The data is inconsistent; a stock correction must be recorded manually.");
            }

            // Stock that has already left the shelf must not be silently erased by undoing its receipt.
            var consumed = lines.Where(line => !line.MedicineBatch!.IsReversibleReceipt).ToList();
            if (consumed.Count > 0)
            {
                return OperationResult<int>.Fail(
                    $"{consumed.Count} of {lines.Count} batch(es) on this purchase have already moved, " +
                    "been written off or been recalled, so the receipt can no longer be reversed. " +
                    "Record an adjustment or supplier return instead.");
            }

            try
            {
                purchase.Reverse(reason, reversedBy, _clock.UtcNow);
            }
            catch (DomainValidationException ex)
            {
                return OperationResult<int>.Fail(ex.Message);
            }

            var now = _clock.UtcNow;
            var businessDate = _clock.BusinessDate;

            foreach (var line in lines)
            {
                var batch = line.MedicineBatch!;
                var quantity = line.QuantityReceived;

                batch.DecreaseQuantity(quantity, now);

                db.StockMovements.Add(StockMovement.Record(
                    line.MedicineId,
                    batch.Id,
                    StockMovementType.ReceiptReversal,
                    -quantity,
                    batch.QuantityOnHand,
                    businessDate,
                    reversedBy,
                    reason,
                    $"REV-{purchase.InvoiceNumber}",
                    now));
            }

            try
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
            {
                return OperationResult<int>.Fail(message);
            }

            return OperationResult<int>.Success(purchase.Id);
        }).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            return OperationResult<PurchaseDto>.Fail(outcome.Errors);
        }

        var reloaded = await ReloadAsync(outcome.Value, cancellationToken).ConfigureAwait(false);

        _logger.LogWarning(
            "Reversed purchase {PurchaseId} ({InvoiceNumber}) by {Operator}: {Reason}",
            reloaded.Id,
            reloaded.InvoiceNumber,
            reversedBy,
            reason);

        return OperationResult<PurchaseDto>.Success(reloaded);
    }

    public async Task<OperationResult<PurchaseDto>> CancelAsync(
        int purchaseId,
        string reason,
        string cancelledBy,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var purchase = await db.Purchases
            .FirstOrDefaultAsync(p => p.Id == purchaseId, cancellationToken)
            .ConfigureAwait(false);

        if (purchase is null)
        {
            return OperationResult<PurchaseDto>.Fail($"Purchase {purchaseId} no longer exists.");
        }

        try
        {
            purchase.Cancel(reason, cancelledBy, _clock.UtcNow);
        }
        catch (DomainValidationException ex)
        {
            return OperationResult<PurchaseDto>.Fail(ex.Message);
        }

        try
        {
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            // A stale read must not be able to write "Cancelled" over a receipt that has already been
            // committed: the stock would still be on the shelf and the reversal would then be refused.
            return OperationResult<PurchaseDto>.Fail(RejectedConcurrency(purchaseId, ex).Errors);
        }

        _logger.LogInformation(
            "Cancelled draft purchase {PurchaseId} by {Operator}: {Reason}",
            purchase.Id,
            cancelledBy,
            reason);

        return OperationResult<PurchaseDto>.Success(await ReloadAsync(purchase.Id, cancellationToken).ConfigureAwait(false));
    }

    /// <summary>
    /// Sums the stored line values so a list total always equals the sum of the rounded line values.
    /// Rounding the aggregate in SQL instead could differ by a paisa from what the operator sees.
    /// </summary>
    private async Task<IReadOnlyList<PurchaseSummaryDto>> WithTotalsAsync(
        MediStockDbContext db,
        IReadOnlyList<PurchaseSummaryDto> page,
        CancellationToken cancellationToken)
    {
        if (page.Count == 0)
        {
            return page;
        }

        var ids = page.Select(summary => summary.Id).ToList();

        var lineValues = await db.PurchaseLines
            .AsNoTracking()
            .Where(line => ids.Contains(line.PurchaseId))
            .Select(line => new { line.PurchaseId, line.Quantity, line.UnitCost })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var totals = lineValues
            .GroupBy(line => line.PurchaseId)
            .ToDictionary(
                group => group.Key,
                group => Money.Round(group.Sum(line => Money.Round(line.Quantity * line.UnitCost))));

        return
        [
            .. page.Select(summary => summary with
            {
                TotalValue = totals.TryGetValue(summary.Id, out var total) ? total : 0m,
            }),
        ];
    }

    /// <summary>
    /// Runs one unit of work under the retrying execution strategy and turns a lost concurrency race
    /// into a refusal rather than a stack trace. The purchase header carries a rowversion (see ADR
    /// 0006), so an operation that read the header before another operation changed it fails the
    /// database's conditional update here and its transaction is never committed - no half-received
    /// delivery, no double reversal, no cancel overwriting a receipt.
    /// </summary>
    private async Task<OperationResult<int>> ExecuteUnitOfWorkAsync(
        MediStockDbContext db,
        int purchaseId,
        Func<Task<OperationResult<int>>> unitOfWork)
    {
        try
        {
            return await db.Database.CreateExecutionStrategy().ExecuteAsync(unitOfWork).ConfigureAwait(false);
        }
        catch (DbUpdateConcurrencyException ex)
        {
            return RejectedConcurrency(purchaseId, ex);
        }
    }

    private OperationResult<int> RejectedConcurrency(int purchaseId, DbUpdateConcurrencyException exception)
    {
        _logger.LogWarning(
            exception,
            "Purchase {PurchaseId} was changed by another operation while this one was in flight. The change was not applied.",
            purchaseId);

        return OperationResult<int>.Fail(
            "Another operator changed this purchase while you were working on it, so your change was not " +
            "saved. Reload the purchase to see its current state and try again.");
    }

    private async Task<PurchaseDto> ReloadAsync(int purchaseId, CancellationToken cancellationToken)
    {
        var purchase = await FindAsync(purchaseId, cancellationToken).ConfigureAwait(false);

        return purchase ?? throw new InvalidOperationException($"Purchase {purchaseId} could not be reloaded after saving.");
    }

    private static string Reference(int purchaseId) => $"PUR-{purchaseId}";

    private static PurchaseDto Project(Purchase purchase) => new()
    {
        Id = purchase.Id,
        SupplierId = purchase.SupplierId,
        SupplierName = purchase.Supplier?.Name ?? string.Empty,
        InvoiceNumber = purchase.InvoiceNumber,
        InvoiceDate = purchase.InvoiceDate,
        Notes = purchase.Notes,
        Status = purchase.Status,
        TotalValue = purchase.TotalValue,
        ReceivedOn = purchase.ReceivedOn,
        ReceivedAtUtc = purchase.ReceivedAtUtc,
        ReceivedBy = purchase.ReceivedBy,
        ClosedAtUtc = purchase.ClosedAtUtc,
        ClosedBy = purchase.ClosedBy,
        StatusReason = purchase.StatusReason,
        CreatedAtUtc = purchase.CreatedAtUtc,
        UpdatedAtUtc = purchase.UpdatedAtUtc,
        Lines =
        [
            .. purchase.Lines
                .OrderBy(line => line.Id)
                .Select(line => new PurchaseLineDto
                {
                    Id = line.Id,
                    MedicineId = line.MedicineId,
                    MedicineName = line.Medicine?.DisplayName ?? string.Empty,
                    BatchNumber = line.BatchNumber,
                    ExpiryDate = line.ExpiryDate,
                    Quantity = line.Quantity,
                    QuantityReceived = line.QuantityReceived,
                    UnitCost = line.UnitCost,
                    SellingPrice = line.SellingPrice,
                    LineTotal = line.LineTotal,
                    MedicineBatchId = line.MedicineBatchId,
                }),
        ],
    };

    private static string? Normalise(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string term)
        => term.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);

    private static bool TryDescribeConstraintViolation(DbUpdateException exception, out string message)
    {
        message = string.Empty;

        if (exception.InnerException is not SqlException sql)
        {
            return false;
        }

        switch (sql.Number)
        {
            case 2601:
            case 2627:
                message = sql.Message.Contains("UX_PurchaseLines", StringComparison.OrdinalIgnoreCase)
                    ? "This purchase already has a line for that medicine and batch number. Combine the quantities into one line."
                    : "A record with these details already exists.";
                return true;

            case 547:
                message = "The change was rejected by a database rule. Check the quantities, dates and prices and try again.";
                return true;

            default:
                return false;
        }
    }
}
