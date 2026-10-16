using MediStock.Application.Common;
using MediStock.Application.Sales;
using MediStock.Domain.Common;
using MediStock.Domain.Inventory;
using MediStock.Domain.Sales;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Sales;

/// <summary>
/// Entity Framework Core implementation of customer returns and invoice cancellation.
/// </summary>
/// <remarks>
/// <para>
/// A return is one transaction. The invoice is re-read and re-checked inside it, the return number is
/// issued, the invoice's returned quantities are updated, the goods that go back on the shelf move the
/// batch balance through the stock ledger, and the return is written - or none of it happens. A return
/// that fails therefore leaves the till and the shelf exactly as they were.
/// </para>
/// <para>
/// The screen's copy of the invoice is never trusted. Two operators can both have the same invoice open,
/// so the quantities and the batch balance are re-read here and the writes are conditional: the line and
/// the allocation carry a rowversion (see <c>docs/decisions/0006-context-lifetime-and-execution-strategy.md</c>),
/// so the operator who loses that race matches no row, the transaction rolls back, and the whole unit of
/// work is repeated and refused with a message naming the real remaining quantity. Without that, two
/// returns could each refund the last unit and put two units back on a shelf that only ever held one.
/// </para>
/// <para>
/// See <c>docs/decisions/0008-sales-returns-and-stock-disposition.md</c>.
/// </para>
/// </remarks>
internal sealed class EfSaleReturnCatalog : ISaleReturnCatalog
{
    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IClock _clock;
    private readonly ISaleCatalog _sales;
    private readonly EfInvoiceNumberAllocator _invoiceNumbers;
    private readonly ILogger<EfSaleReturnCatalog> _logger;

    public EfSaleReturnCatalog(
        IDbContextFactory<MediStockDbContext> factory,
        IClock clock,
        ISaleCatalog sales,
        EfInvoiceNumberAllocator invoiceNumbers,
        ILogger<EfSaleReturnCatalog> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _sales = sales ?? throw new ArgumentNullException(nameof(sales));
        _invoiceNumbers = invoiceNumbers ?? throw new ArgumentNullException(nameof(invoiceNumbers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OperationResult<SalesReturnDto>> RecordAsync(
        SalesReturnDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var request = draft.Normalise();

        if (request.Lines.Count == 0)
        {
            return OperationResult<SalesReturnDto>.Fail(
                "A return must name at least one quantity to bring back. Add a line and try again.");
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
                    var sale = await InvoiceAsync(db, request.SaleId, cancellationToken).ConfigureAwait(false);
                    if (sale is null)
                    {
                        return OperationResult<int>.Fail(
                            "That invoice could not be found, so nothing was returned. Search the sales history for the invoice the customer has.");
                    }

                    var batches = await LoadBatchesAsync(db, sale, cancellationToken).ConfigureAwait(false);

                    // Issued inside the transaction: a return that fails to commit gives the number back.
                    var returnNumber = await _invoiceNumbers
                        .AllocateAsync(db, businessDate, InvoiceSequence.ReturnsPrefix, cancellationToken)
                        .ConfigureAwait(false);

                    var salesReturn = SalesReturn.Record(
                        returnNumber,
                        businessDate,
                        request.ReturnedBy,
                        sale,
                        request.Reason,
                        [
                            .. request.Lines.Select(line => new SalesReturnLineRequest(
                                line.LineNumber,
                                line.MedicineBatchId,
                                line.Quantity,
                                line.Disposition,
                                line.Notes)),
                        ],
                        batches,
                        request.Notes,
                        request.RefundMethod,
                        now);

                    // Only an explicit restock moves stock, and it moves it before the return is written
                    // so that whatever the batch ends up holding is what its ledger will say it holds.
                    var restocked = Restock(salesReturn, batches, businessDate, now);

                    db.SalesReturns.Add(salesReturn);

                    // The return must exist before its movements can quote its number.
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    foreach (var line in restocked)
                    {
                        var batch = batches[line.MedicineBatchId];

                        db.StockMovements.Add(StockMovement.Record(
                            batch.MedicineId,
                            batch.Id,
                            StockMovementType.SalesReturn,
                            line.Quantity,
                            batch.QuantityOnHand,
                            businessDate,
                            request.ReturnedBy,
                            reason: salesReturn.Reason,
                            reference: Reference(salesReturn.Id),
                            nowUtc: now));
                    }

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return OperationResult<int>.Success(salesReturn.Id);
                }
                catch (DomainException ex)
                {
                    // Everything the operator can put right: a quantity that is no longer available, a
                    // batch the line never sold, a batch that can no longer take stock back.
                    return OperationResult<int>.Fail(ex.Message);
                }
                catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
                {
                    return OperationResult<int>.Fail(message);
                }
            },
            "Another operator recorded a return against this invoice at the same moment, so this return was "
            + "not saved and nothing was refunded. Reopen the invoice and check what is left to return.",
            _logger,
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            _logger.LogInformation(
                "Refused to record a return of {LineCount} line(s) against invoice {SaleId}: {Reasons}",
                request.Lines.Count,
                request.SaleId,
                string.Join(" | ", outcome.Errors));

            return OperationResult<SalesReturnDto>.Fail(outcome.Errors);
        }

        var reloaded = await FindAsync(outcome.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Return {outcome.Value} could not be reloaded after committing.");

        _logger.LogInformation(
            "Recorded return {ReturnNumber} of {Refund:0.00} against invoice {InvoiceNumber}: {LineCount} line(s), "
            + "{Restocked:0.###} unit(s) restocked.",
            reloaded.ReturnNumber,
            reloaded.RefundTotal,
            reloaded.InvoiceNumber,
            reloaded.Lines.Count,
            reloaded.RestockedQuantity);

        return OperationResult<SalesReturnDto>.Success(reloaded);
    }

    public async Task<SalesReturnDto?> FindAsync(
        int salesReturnId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var salesReturn = await Load(db.SalesReturns)
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == salesReturnId, cancellationToken)
            .ConfigureAwait(false);

        return salesReturn is null ? null : SalesReturnProjection.Detail(salesReturn);
    }

    public async Task<SalesReturnDto?> FindByReturnNumberAsync(
        string returnNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(returnNumber))
        {
            return null;
        }

        var wanted = returnNumber.Trim();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var salesReturn = await Load(db.SalesReturns)
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.ReturnNumber == wanted, cancellationToken)
            .ConfigureAwait(false);

        return salesReturn is null ? null : SalesReturnProjection.Detail(salesReturn);
    }

    public async Task<IReadOnlyList<SalesReturnDto>> SearchAsync(
        SalesReturnQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = db.SalesReturns.AsNoTracking();

        if (query.FromBusinessDate is { } from)
        {
            source = source.Where(salesReturn => salesReturn.BusinessDate >= from);
        }

        if (query.ToBusinessDate is { } to)
        {
            source = source.Where(salesReturn => salesReturn.BusinessDate <= to);
        }

        if (query.SaleId is { } saleId)
        {
            source = source.Where(salesReturn => salesReturn.SaleId == saleId);
        }

        if (query.Disposition is { } disposition)
        {
            source = source.Where(salesReturn =>
                salesReturn.Lines.Any(line => line.Disposition == disposition));
        }

        if (Normalise(query.Term) is { } term)
        {
            // A return is looked up by its own number, by the invoice the customer brought back, or by
            // the medicine on it - whichever the operator happens to be holding.
            var pattern = $"%{EscapeLike(term)}%";
            source = source.Where(salesReturn =>
                EF.Functions.Like(salesReturn.ReturnNumber, pattern)
                || EF.Functions.Like(salesReturn.Sale.InvoiceNumber, pattern)
                || salesReturn.Lines.Any(line => EF.Functions.Like(line.SaleLine.MedicineName, pattern)));
        }

        var take = Math.Clamp(query.Take, 1, SalesReturnQuery.MaxResults);

        var returns = await Load(source)
            .OrderByDescending(salesReturn => salesReturn.BusinessDate)
            .ThenByDescending(salesReturn => salesReturn.Id)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. returns.Select(SalesReturnProjection.Detail)];
    }

    public async Task<string> PreviewReturnNumberAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await _invoiceNumbers
            .PreviewAsync(db, _clock.BusinessDate, InvoiceSequence.ReturnsPrefix, cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<OperationResult<SaleDto>> CancelSaleAsync(
        int saleId,
        string cancelledBy,
        string reason,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var outcome = await UnitOfWorkRetry.ExecuteAsync(
            db,
            async () =>
            {
                db.ChangeTracker.Clear();

                var now = _clock.UtcNow;

                await using var transaction = await db.Database
                    .BeginTransactionAsync(cancellationToken)
                    .ConfigureAwait(false);

                try
                {
                    var sale = await InvoiceAsync(db, saleId, cancellationToken).ConfigureAwait(false);
                    if (sale is null)
                    {
                        return OperationResult<int>.Fail("That invoice could not be found, so it was not cancelled.");
                    }

                    // Refuses an invoice that still has stock out with the customer, and one that was
                    // already cancelled. The rowversion on the invoice makes that decision conditional.
                    sale.Cancel(cancelledBy, reason, now);

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return OperationResult<int>.Success(sale.Id);
                }
                catch (DomainException ex)
                {
                    return OperationResult<int>.Fail(ex.Message);
                }
                catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
                {
                    return OperationResult<int>.Fail(message);
                }
            },
            "Another operator changed this invoice at the same moment, so it was not cancelled. Reopen it and try again.",
            _logger,
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            _logger.LogInformation(
                "Refused to cancel invoice {SaleId}: {Reasons}",
                saleId,
                string.Join(" | ", outcome.Errors));

            return OperationResult<SaleDto>.Fail(outcome.Errors);
        }

        // Projected through the sales catalog so a cancelled invoice is described by exactly the same
        // code as any other invoice.
        var reloaded = await _sales.FindAsync(outcome.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Sale {outcome.Value} could not be reloaded after cancelling.");

        _logger.LogInformation(
            "Cancelled invoice {InvoiceNumber} ({Reason}).",
            reloaded.InvoiceNumber,
            reloaded.CancellationReason);

        return OperationResult<SaleDto>.Success(reloaded);
    }

    /// <summary>
    /// Puts the quantities the operator chose to restock back into their batches, and returns the lines
    /// that did move stock so the caller can write one movement per line.
    /// </summary>
    /// <remarks>
    /// Quarantined and destroyed goods move nothing, so the cached batch balance stays equal to the sum
    /// of the ledger. A restock that the batch can no longer accept - because it expired, or was written
    /// off or recalled - is refused by the batch itself rather than silently dropped.
    /// </remarks>
    private static List<SalesReturnLine> Restock(
        SalesReturn salesReturn,
        IReadOnlyDictionary<int, MedicineBatch> batches,
        DateOnly businessDate,
        DateTimeOffset now)
    {
        var restocked = new List<SalesReturnLine>();

        foreach (var line in salesReturn.Lines)
        {
            if (line.Disposition != ReturnDisposition.Restock)
            {
                continue;
            }

            batches[line.MedicineBatchId].RestoreQuantity(line.Quantity, businessDate, now);
            restocked.Add(line);
        }

        return restocked;
    }

    private static string Reference(int salesReturnId) => $"RET-{salesReturnId}";

    /// <summary>
    /// Loads an invoice with everything a return or a cancellation has to check: its lines and what each
    /// line sold out of which batch, and the returns already recorded against it.
    /// </summary>
    private static Task<Sale?> InvoiceAsync(MediStockDbContext db, int saleId, CancellationToken cancellationToken)
        => db.Sales
            .Include(sale => sale.Lines)
            .ThenInclude(line => line.Allocations)
            .Include(sale => sale.Returns)
            .AsSplitQuery()
            .FirstOrDefaultAsync(sale => sale.Id == saleId, cancellationToken);

    /// <summary>
    /// Loads every batch this invoice sold from, tracked, so a restock can move a balance that the
    /// database has not seen anyone else touch.
    /// </summary>
    private static async Task<Dictionary<int, MedicineBatch>> LoadBatchesAsync(
        MediStockDbContext db,
        Sale sale,
        CancellationToken cancellationToken)
    {
        var batchIds = sale.Lines
            .SelectMany(line => line.Allocations)
            .Select(allocation => allocation.MedicineBatchId)
            .Distinct()
            .ToList();

        var batches = await db.MedicineBatches
            .Where(batch => batchIds.Contains(batch.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return batches.ToDictionary(batch => batch.Id);
    }

    /// <summary>Loads a return with the invoice it credits and the batch each line came from.</summary>
    private static IQueryable<SalesReturn> Load(IQueryable<SalesReturn> source) => source
        .Include(salesReturn => salesReturn.Sale)
        .Include(salesReturn => salesReturn.Lines)
        .ThenInclude(line => line.SaleLine)
        .Include(salesReturn => salesReturn.Lines)
        .ThenInclude(line => line.MedicineBatch)
        .AsSplitQuery();

    private static string? Normalise(string? value)
        => string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static string EscapeLike(string term)
        => term.Replace("[", "[[]", StringComparison.Ordinal)
            .Replace("%", "[%]", StringComparison.Ordinal)
            .Replace("_", "[_]", StringComparison.Ordinal);

    private static bool TryDescribeConstraintViolation(DbUpdateException exception, out string message)
    {
        message = string.Empty;

        if (!SqlFailure.TryClassify(exception, out var kind, out var sql))
        {
            return false;
        }

        if (kind == SqlFailureKind.Duplicate)
        {
            message = SqlFailure.NamesAtLeastOne(sql, "UX_SalesReturns_ReturnNumber")
                ? "That return number has already been used. The return was not saved; try again."
                : "A record with these details already exists, so the return was not saved.";

            return true;
        }

        // The check constraints on the invoice and its allocations are the last line of defence: they
        // are what stops a return that was raced by another operator from refunding money that is no
        // longer owed. The domain refuses it first; this is the message if it ever does not.
        message = SqlFailure.NamesAtLeastOne(sql, "CK_SaleLines", "CK_SaleLineBatchAllocations")
            ? "The invoice has less left to return than this return assumes, so nothing was refunded. "
              + "Reopen the invoice and check the quantities that are still returnable."
            : "The return was rejected by a database rule, so nothing was refunded. Check the quantities "
              + "and what is to be done with the goods, then try again.";

        return true;
    }
}
