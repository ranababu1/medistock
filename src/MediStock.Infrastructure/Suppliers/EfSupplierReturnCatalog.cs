using System.Globalization;
using MediStock.Application.Common;
using MediStock.Application.Identity;
using MediStock.Application.Suppliers;
using MediStock.Domain.Common;
using MediStock.Domain.Identity;
using MediStock.Domain.Inventory;
using MediStock.Domain.Sales;
using MediStock.Domain.Suppliers;
using MediStock.Infrastructure.Identity;
using MediStock.Infrastructure.Persistence;
using MediStock.Infrastructure.Sales;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Suppliers;

/// <summary>
/// Entity Framework Core implementation of supplier returns.
/// </summary>
/// <remarks>
/// <para>
/// A return is one transaction. The supplier is re-read inside it, the batches are loaded tracked, the
/// return number is issued, every batch gives up its quantity, and one movement per line is appended to
/// the stock ledger - or none of it happens. A return that fails leaves the shelf and the supplier
/// history exactly as they were.
/// </para>
/// <para>
/// The screen's copy of the batch balances is never trusted. Two operators can both have the same
/// batches open, so the quantities are re-read here and the writes are conditional: the batch carries a
/// rowversion, so the operator who loses that race matches no row, the transaction rolls back, and the
/// whole unit of work is repeated and refused with a message naming what the batch really holds.
/// </para>
/// <para>
/// See <c>docs/decisions/0011-stock-corrections.md</c>.
/// </para>
/// </remarks>
internal sealed class EfSupplierReturnCatalog : ISupplierReturnCatalog
{
    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IClock _clock;
    private readonly IUserSession _session;
    private readonly EfInvoiceNumberAllocator _invoiceNumbers;
    private readonly ILogger<EfSupplierReturnCatalog> _logger;

    public EfSupplierReturnCatalog(
        IDbContextFactory<MediStockDbContext> factory,
        IClock clock,
        IUserSession session,
        EfInvoiceNumberAllocator invoiceNumbers,
        ILogger<EfSupplierReturnCatalog> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _invoiceNumbers = invoiceNumbers ?? throw new ArgumentNullException(nameof(invoiceNumbers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<OperationResult<SupplierReturnDto>> RecordAsync(
        SupplierReturnDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var request = draft.Normalise();

        if (request.Lines.Count == 0)
        {
            return OperationResult<SupplierReturnDto>.Fail(
                "A supplier return must send back at least one quantity. Add a line and try again.");
        }

        if (request.SupplierId <= 0)
        {
            return OperationResult<SupplierReturnDto>.Fail(
                "A supplier return must name the supplier the goods go back to. Choose the supplier first.");
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
                    var supplier = await db.Suppliers
                        .AsNoTracking()
                        .FirstOrDefaultAsync(candidate => candidate.Id == request.SupplierId, cancellationToken)
                        .ConfigureAwait(false);

                    if (supplier is null)
                    {
                        return OperationResult<int>.Fail(
                            "That supplier could not be found, so nothing was returned. Choose the supplier the "
                            + "goods were bought from.");
                    }

                    var batches = await LoadBatchesAsync(db, request, cancellationToken).ConfigureAwait(false);

                    // Issued inside the transaction: a return that fails to commit gives the number back.
                    var returnNumber = await _invoiceNumbers
                        .AllocateAsync(db, businessDate, InvoiceSequence.SupplierReturnsPrefix, cancellationToken)
                        .ConfigureAwait(false);

                    var supplierReturn = SupplierReturn.Record(
                        returnNumber,
                        businessDate,
                        request.ReturnedBy,
                        supplier.Id,
                        supplier.Name,
                        request.Reason,
                        [
                            .. request.Lines.Select(line => new SupplierReturnLineRequest(
                                line.MedicineBatchId,
                                line.Quantity,
                                line.Notes)),
                        ],
                        batches,
                        request.Notes,
                        request.SupplierCreditNoteNumber,
                        now);

                    db.SupplierReturns.Add(supplierReturn);

                    // The return must exist before its movements can quote its number.
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                    foreach (var line in supplierReturn.Lines)
                    {
                        var batch = batches[line.MedicineBatchId];

                        db.StockMovements.Add(StockMovement.Record(
                            batch.MedicineId,
                            batch.Id,
                            StockMovementType.SupplierReturn,
                            -line.Quantity,
                            batch.QuantityOnHand,
                            businessDate,
                            supplierReturn.ReturnedBy,
                            reason: supplierReturn.Reason,
                            reference: Reference(supplierReturn.Id),
                            nowUtc: now));
                    }

                    var (actor, actorId) = OperatorOf(supplierReturn.ReturnedBy);

                    AuditJournal.Stage(
                        db,
                        new AuditRecord(
                            AuditAction.SupplierReturnRecorded,
                            $"Supplier return {supplierReturn.ReturnNumber} was recorded for {supplier.Name}: "
                            + $"{supplierReturn.Lines.Count} line(s), {supplierReturn.ReturnedQuantity:0.###} unit(s).")
                        {
                            EntityType = "SupplierReturn",
                            EntityId = supplierReturn.ReturnNumber,
                            Reason = supplierReturn.Reason,
                            Detail = DescribeReturn(supplierReturn),
                        },
                        actor,
                        actorId);

                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                    await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                    return OperationResult<int>.Success(supplierReturn.Id);
                }
                catch (DomainException ex)
                {
                    // Everything the operator can put right: a batch bought from a different supplier, a
                    // quantity no longer on the shelf, a batch blocked from being moved.
                    return OperationResult<int>.Fail(ex.Message);
                }
                catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
                {
                    return OperationResult<int>.Fail(message);
                }
            },
            "Another operator was moving these batches at the same moment, so this supplier return was not "
            + "saved and nothing left the shelf. Reopen the batches and try again.",
            _logger,
            cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            _logger.LogInformation(
                "Refused to record a supplier return of {LineCount} line(s) for supplier {SupplierId}: {Reasons}",
                request.Lines.Count,
                request.SupplierId,
                string.Join(" | ", outcome.Errors));

            return OperationResult<SupplierReturnDto>.Fail(outcome.Errors);
        }

        var reloaded = await FindAsync(outcome.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Supplier return {outcome.Value} could not be reloaded after committing.");

        _logger.LogInformation(
            "Recorded supplier return {ReturnNumber} for supplier {SupplierName}: {LineCount} line(s), "
            + "{Quantity:0.###} unit(s) sent back.",
            reloaded.ReturnNumber,
            reloaded.SupplierName,
            reloaded.Lines.Count,
            reloaded.Lines.Sum(line => line.Quantity));

        return OperationResult<SupplierReturnDto>.Success(reloaded);
    }

    public async Task<SupplierReturnDto?> FindAsync(
        int supplierReturnId,
        CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var supplierReturn = await Load(db.SupplierReturns)
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == supplierReturnId, cancellationToken)
            .ConfigureAwait(false);

        return supplierReturn is null ? null : SupplierReturnProjection.Detail(
            supplierReturn,
            await SupplierNameAsync(db, supplierReturn.SupplierId, cancellationToken).ConfigureAwait(false));
    }

    public async Task<IReadOnlyList<SupplierReturnSummaryDto>> SearchAsync(
        SupplierReturnQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = db.SupplierReturns.AsNoTracking();

        if (query.FromBusinessDate is { } from)
        {
            source = source.Where(supplierReturn => supplierReturn.BusinessDate >= from);
        }

        if (query.ToBusinessDate is { } to)
        {
            source = source.Where(supplierReturn => supplierReturn.BusinessDate <= to);
        }

        if (query.SupplierId is { } supplierId)
        {
            source = source.Where(supplierReturn => supplierReturn.SupplierId == supplierId);
        }

        if (Normalise(query.Term) is { } term)
        {
            // A return is looked up by its own number, by the credit note the supplier issued, or by the
            // supplier it went back to - whichever the operator happens to be holding.
            var pattern = $"%{EscapeLike(term)}%";
            source = source.Where(supplierReturn =>
                EF.Functions.Like(supplierReturn.ReturnNumber, pattern)
                || (supplierReturn.SupplierCreditNoteNumber != null
                    && EF.Functions.Like(supplierReturn.SupplierCreditNoteNumber, pattern))
                || db.Suppliers.Any(supplier =>
                    supplier.Id == supplierReturn.SupplierId
                    && EF.Functions.Like(supplier.Name, pattern)));
        }

        var take = Math.Clamp(query.Take, 1, SupplierReturnQuery.MaxResults);

        var rows = await source
            .OrderByDescending(supplierReturn => supplierReturn.BusinessDate)
            .ThenByDescending(supplierReturn => supplierReturn.Id)
            .Take(take)
            .Select(supplierReturn => new
            {
                supplierReturn.Id,
                supplierReturn.ReturnNumber,
                supplierReturn.BusinessDate,
                supplierReturn.ReturnedBy,
                supplierReturn.SupplierId,
                SupplierName = db.Suppliers
                    .Where(supplier => supplier.Id == supplierReturn.SupplierId)
                    .Select(supplier => supplier.Name)
                    .FirstOrDefault(),
                supplierReturn.TotalCostValue,
                ReturnedQuantity = supplierReturn.Lines.Sum(line => line.Quantity),
                supplierReturn.Reason,
            })
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return
        [
            .. rows.Select(row => new SupplierReturnSummaryDto(
                row.Id,
                row.ReturnNumber,
                row.BusinessDate,
                row.ReturnedBy,
                row.SupplierId,
                row.SupplierName ?? string.Empty,
                row.TotalCostValue,
                row.ReturnedQuantity,
                row.Reason)),
        ];
    }

    public async Task<string> PreviewReturnNumberAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await _invoiceNumbers
            .PreviewAsync(db, _clock.BusinessDate, InvoiceSequence.SupplierReturnsPrefix, cancellationToken)
            .ConfigureAwait(false);
    }

    private static string Reference(int supplierReturnId) => $"SRET-{supplierReturnId}";

    /// <summary>
    /// What went back and what it was worth, for the audit detail. The credit value matters more than the
    /// count: it is the figure the supplier's statement has to agree with.
    /// </summary>
    private static string DescribeReturn(SupplierReturn supplierReturn)
        => string.Format(
            CultureInfo.InvariantCulture,
            "{0:0.###} unit(s) across {1} line(s); credit expected {2:0.00} at cost.",
            supplierReturn.ReturnedQuantity,
            supplierReturn.Lines.Count,
            supplierReturn.TotalCostValue);

    /// <summary>
    /// The name a stock movement is signed with, and the account it belongs to when that name really is the
    /// signed-in one. The operator box is free text, so the two can differ; the trail records what was typed
    /// and only links an account when it matches.
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

    /// <summary>
    /// Loads every batch the return names, tracked, so the quantity leaves a balance that the database has
    /// not seen anyone else touch.
    /// </summary>
    private static async Task<Dictionary<int, MedicineBatch>> LoadBatchesAsync(
        MediStockDbContext db,
        SupplierReturnDraft request,
        CancellationToken cancellationToken)
    {
        var batchIds = request.Lines
            .Select(line => line.MedicineBatchId)
            .Distinct()
            .ToList();

        var batches = await db.MedicineBatches
            .Where(batch => batchIds.Contains(batch.Id))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return batches.ToDictionary(batch => batch.Id);
    }

    /// <summary>Loads a return with the batch and medicine each line came from, for the detail view.</summary>
    private static IQueryable<SupplierReturn> Load(IQueryable<SupplierReturn> source) => source
        .Include(supplierReturn => supplierReturn.Lines)
        .ThenInclude(line => line.MedicineBatch)
        .ThenInclude(batch => batch.Medicine)
        .AsSplitQuery();

    private static async Task<string> SupplierNameAsync(
        MediStockDbContext db,
        int supplierId,
        CancellationToken cancellationToken)
        => await db.Suppliers
            .AsNoTracking()
            .Where(supplier => supplier.Id == supplierId)
            .Select(supplier => supplier.Name)
            .FirstOrDefaultAsync(cancellationToken)
            .ConfigureAwait(false) ?? string.Empty;

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
            message = SqlFailure.NamesAtLeastOne(sql, "UX_SupplierReturns_ReturnNumber")
                ? "That return number has already been used. The return was not saved; try again."
                : "A record with these details already exists, so the return was not saved.";

            return true;
        }

        // The check constraints are the last line of defence: the domain refuses the impossible first -
        // a quantity the batch is not holding, a blank reason - and this is the message if a race got
        // past it.
        message = SqlFailure.NamesAtLeastOne(sql, "CK_SupplierReturns", "CK_SupplierReturnLines")
            ? "The return was rejected by a database rule, so nothing left the shelf. Check the batches, "
              + "the quantities and the supplier it is being credited to, then try again."
            : "The return was rejected by a database rule, so nothing left the shelf. Check the batches and "
              + "the quantities, then try again.";

        return true;
    }
}