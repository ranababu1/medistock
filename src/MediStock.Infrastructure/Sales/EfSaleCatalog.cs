using System.Globalization;
using MediStock.Application.Common;
using MediStock.Application.Identity;
using MediStock.Application.Sales;
using MediStock.Domain.Common;
using MediStock.Domain.Identity;
using MediStock.Domain.Inventory;
using MediStock.Domain.Sales;
using MediStock.Infrastructure.Identity;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Sales;

/// <summary>
/// Entity Framework Core implementation of the point of sale.
/// </summary>
/// <remarks>
/// <para>
/// A checkout is one transaction. The basket is priced, first-expiry-first-out is applied, the invoice
/// number is issued, the batches are reduced, the invoice is written and the stock movements are
/// recorded or none of it happens. Stock is only reduced inside that transaction, so a failure can
/// never leave a sold-out shelf behind an invoice that was never printed.
/// </para>
/// <para>
/// The connection retries transient faults, and EF Core refuses to run statements inside a user
/// transaction unless the whole unit of work is handed to its execution strategy. Every attempt
/// therefore starts from a clean change tracker and repeats the entire basket build rather than
/// resuming a half-built one.
/// </para>
/// <para>
/// The medicine batches carry a database rowversion (see
/// <c>docs/decisions/0006-context-lifetime-and-execution-strategy.md</c>), so the quantity write is
/// conditional: two tills selling the last unit at the same moment cannot both succeed. The loser of that
/// race has not committed anything, so the whole basket is read, priced and written again a bounded number
/// of times before the operator is told that the stock moved under them - a basket is only ever refused
/// when the stock really is short.
/// </para>
/// </remarks>
internal sealed class EfSaleCatalog : ISaleCatalog
{
    private readonly IDbContextFactory<MediStockDbContext> _factory;
    private readonly IClock _clock;
    private readonly IUserSession _session;
    private readonly EfInvoiceNumberAllocator _invoiceNumbers;
    private readonly ILogger<EfSaleCatalog> _logger;

    public EfSaleCatalog(
        IDbContextFactory<MediStockDbContext> factory,
        IClock clock,
        IUserSession session,
        EfInvoiceNumberAllocator invoiceNumbers,
        ILogger<EfSaleCatalog> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _invoiceNumbers = invoiceNumbers ?? throw new ArgumentNullException(nameof(invoiceNumbers));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<SaleQuote> QuoteAsync(SaleDraft draft, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var request = draft.Normalise();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var basket = await BuildAsync(db, request, cancellationToken).ConfigureAwait(false);
        var totals = SaleTotals.From(basket.Lines);

        return new SaleQuote
        {
            IsCheckoutReady = basket.Blockers.Count == 0,
            Blockers = basket.Blockers,
            InvoicePreview = await _invoiceNumbers
                .PreviewAsync(db, _clock.BusinessDate, InvoiceSequence.SalesPrefix, cancellationToken)
                .ConfigureAwait(false),
            Lines = [.. basket.Lines.Select(Project)],
            SubTotal = totals.SubTotal,
            DiscountTotal = totals.DiscountTotal,
            TaxableTotal = totals.TaxableTotal,
            TaxTotal = totals.TaxTotal,
            Total = totals.Total,
            CostTotal = totals.CostTotal,
        };
    }

    public async Task<OperationResult<SaleDto>> CheckoutAsync(
        SaleDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        var request = draft.Normalise();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var outcome = await ExecuteUnitOfWorkAsync(db, async () =>
        {
            // See the class remarks: a retry repeats the whole unit of work, never part of one.
            db.ChangeTracker.Clear();

            var businessDate = _clock.BusinessDate;
            var now = _clock.UtcNow;

            await using var transaction = await db.Database
                .BeginTransactionAsync(cancellationToken)
                .ConfigureAwait(false);

            try
            {
                var basket = await BuildAsync(db, request, cancellationToken).ConfigureAwait(false);

                if (basket.Blockers.Count > 0)
                {
                    return OperationResult<int>.Fail(basket.Blockers);
                }

                var payments = request.Payments
                    .Select(payment => Payment.Record(payment.Method, payment.Amount, payment.Reference, now))
                    .ToList();

                // Issued inside the transaction: a sale that fails to commit gives the number back.
                var invoiceNumber = await _invoiceNumbers
                    .AllocateAsync(db, businessDate, InvoiceSequence.SalesPrefix, cancellationToken)
                    .ConfigureAwait(false);

                var sale = Sale.Complete(
                    invoiceNumber,
                    businessDate,
                    request.SoldBy,
                    basket.Lines,
                    payments,
                    request.CustomerName,
                    request.CustomerPhone,
                    request.Notes,
                    now);

                foreach (var promise in basket.Promises)
                {
                    promise.Batch.DecreaseQuantity(promise.Quantity, now);
                }

                db.Sales.Add(sale);

                // The invoice must exist before its movements can quote its number.
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                foreach (var promise in basket.Promises)
                {
                    db.StockMovements.Add(StockMovement.Record(
                        promise.Batch.MedicineId,
                        promise.Batch.Id,
                        StockMovementType.Sale,
                        -promise.Quantity,
                        promise.Batch.QuantityOnHand,
                        businessDate,
                        sale.SoldBy,
                        reason: null,
                        reference: Reference(sale.Id),
                        nowUtc: now));
                }

                // Staged before the commit so the row and the sale are one unit: an audit trail that could
                // miss a completed sale would be worse than none.
                var (actor, actorId) = OperatorOf(sale.SoldBy);
                var saleTotals = SaleTotals.From(basket.Lines);

                AuditJournal.Stage(
                    db,
                    new AuditRecord(
                        AuditAction.SaleCompleted,
                        $"Sale {sale.InvoiceNumber} was completed for {sale.Lines.Count} line(s).")
                    {
                        EntityType = "Sale",
                        EntityId = sale.InvoiceNumber,
                        Detail = string.Format(
                            CultureInfo.InvariantCulture,
                            "Total {0:0.00} across {1} batch allocation(s), paid by {2}.",
                            saleTotals.Total,
                            basket.Promises.Count,
                            DescribePayments(request)),
                    },
                    actor,
                    actorId);

                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);

                return OperationResult<int>.Success(sale.Id);
            }
            catch (DomainException ex)
            {
                // Includes a short pick discovered here and a settlement that no longer matches the
                // re-priced basket. Both are messages for the operator, and neither may be committed.
                return OperationResult<int>.Fail(ex.Message);
            }
            catch (DbUpdateException ex) when (TryDescribeConstraintViolation(ex, out var message))
            {
                return OperationResult<int>.Fail(message);
            }
        }, cancellationToken).ConfigureAwait(false);

        if (!outcome.Succeeded)
        {
            _logger.LogInformation(
                "Refused to check out a basket of {LineCount} line(s): {Reasons}",
                request.Lines.Count,
                string.Join(" | ", outcome.Errors));

            return OperationResult<SaleDto>.Fail(outcome.Errors);
        }

        var reloaded = await FindAsync(outcome.Value, cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Sale {outcome.Value} could not be reloaded after committing.");

        _logger.LogInformation(
            "Completed sale {SaleId} ({InvoiceNumber}): {LineCount} line(s), total {Total:0.00}.",
            reloaded.Id,
            reloaded.InvoiceNumber,
            reloaded.Lines.Count,
            reloaded.Total);

        return OperationResult<SaleDto>.Success(reloaded);
    }

    public async Task<SaleDto?> FindAsync(int saleId, CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var sale = await Query(db)
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.Id == saleId, cancellationToken)
            .ConfigureAwait(false);

        return sale is null ? null : Project(sale);
    }

    public async Task<SaleDto?> FindByInvoiceNumberAsync(
        string invoiceNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(invoiceNumber))
        {
            return null;
        }

        var wanted = invoiceNumber.Trim();

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var sale = await Query(db)
            .AsNoTracking()
            .FirstOrDefaultAsync(candidate => candidate.InvoiceNumber == wanted, cancellationToken)
            .ConfigureAwait(false);

        return sale is null ? null : Project(sale);
    }

    public async Task<IReadOnlyList<SaleDto>> SearchAsync(
        SaleQuery query,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(query);

        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        var source = db.Sales.AsNoTracking();

        if (query.FromBusinessDate is { } from)
        {
            source = source.Where(sale => sale.BusinessDate >= from);
        }

        if (query.ToBusinessDate is { } to)
        {
            source = source.Where(sale => sale.BusinessDate <= to);
        }

        if (query.Status is { } status)
        {
            source = source.Where(sale => sale.Status == status);
        }

        if (query.PaymentMethod is { } method)
        {
            source = source.Where(sale => sale.Payments.Any(payment => payment.Method == method));
        }

        if (Normalise(query.Term) is { } term)
        {
            // A barcode-length string is almost certainly an invoice number the operator scanned; it is
            // matched against the invoice number first and the wider text fields only as a fallback.
            var pattern = $"%{EscapeLike(term)}%";
            source = source.Where(sale =>
                EF.Functions.Like(sale.InvoiceNumber, pattern)
                || (sale.CustomerName != null && EF.Functions.Like(sale.CustomerName, pattern))
                || (sale.CustomerPhone != null && EF.Functions.Like(sale.CustomerPhone, pattern))
                || sale.Lines.Any(line => EF.Functions.Like(line.MedicineName, pattern)));
        }

        var take = Math.Clamp(query.Take, 1, SaleQuery.MaxResults);

        // Loaded with their lines and payments rather than projected in SQL: the history list shows the
        // same shape as the detail view, so a figure on a row cannot differ from the invoice it opens.
        var sales = await Load(source)
            .OrderByDescending(sale => sale.SoldAtUtc)
            .ThenByDescending(sale => sale.Id)
            .Take(take)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        return [.. sales.Select(Project)];
    }

    public async Task<string> PreviewInvoiceNumberAsync(CancellationToken cancellationToken = default)
    {
        await using var db = await _factory.CreateDbContextAsync(cancellationToken).ConfigureAwait(false);

        return await _invoiceNumbers
            .PreviewAsync(db, _clock.BusinessDate, InvoiceSequence.SalesPrefix, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// The basket as it would be rung up. Built once and used by both the quote and the checkout, so
    /// what the operator was shown and what the customer is charged cannot be computed differently.
    /// </summary>
    private async Task<Basket> BuildAsync(
        MediStockDbContext db,
        SaleDraft draft,
        CancellationToken cancellationToken)
    {
        var businessDate = _clock.BusinessDate;
        var blockers = new List<string>();
        var lines = new List<SaleLine>();
        var promised = new Dictionary<int, decimal>();

        if (draft.Lines.Count == 0)
        {
            blockers.Add("The basket is empty. Scan or search for a medicine to begin.");
            return new Basket(blockers, lines, []);
        }

        var medicineIds = draft.Lines.Select(line => line.MedicineId).Distinct().ToList();

        var medicines = await db.Medicines
            .Where(medicine => medicineIds.Contains(medicine.Id))
            .ToDictionaryAsync(medicine => medicine.Id, cancellationToken)
            .ConfigureAwait(false);

        var stock = await db.MedicineBatches
            .Where(batch => medicineIds.Contains(batch.MedicineId))
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);

        var byMedicine = stock
            .GroupBy(batch => batch.MedicineId)
            .ToDictionary(group => group.Key, group => group.ToList());

        var lineNumber = 1;

        foreach (var line in draft.Lines)
        {
            if (!medicines.TryGetValue(line.MedicineId, out var medicine))
            {
                blockers.Add($"Medicine #{line.MedicineId} is not in the catalogue.");
                continue;
            }

            if (!medicine.IsActive)
            {
                blockers.Add($"'{medicine.DisplayName}' is no longer active and cannot be sold.");
                continue;
            }

            var candidates = byMedicine.TryGetValue(line.MedicineId, out var found) ? found : [];
            var sellable = candidates.Where(batch => ExpiryPolicy.IsSellable(batch, businessDate)).ToList();
            var onHand = sellable.Sum(Remaining);

            if (onHand < line.Quantity)
            {
                blockers.Add(
                    $"Only {onHand:0.###} of '{medicine.DisplayName}' can be dispensed today, but the basket asks "
                    + $"for {line.Quantity:0.###}. Expired, recalled and written-off stock is never sold.");
                continue;
            }

            var allocations = new List<BatchAllocation>();
            var remaining = line.Quantity;

            if (line.PreferredBatchId is { } preferredId)
            {
                var preferred = sellable.FirstOrDefault(batch => batch.Id == preferredId);
                if (preferred is null)
                {
                    blockers.Add($"Batch #{preferredId} of '{medicine.DisplayName}' cannot be dispensed today.");
                    continue;
                }

                var fromPreferred = Math.Min(Remaining(preferred), remaining);
                allocations.Add(new BatchAllocation(
                    preferred.Id, preferred.BatchNumber, preferred.ExpiryDate, fromPreferred));
                remaining -= fromPreferred;
            }

            if (remaining > 0m)
            {
                var rest = sellable.Where(batch => batch.Id != line.PreferredBatchId);
                allocations.AddRange(FefoAllocator.Allocate(Availability(rest), remaining, businessDate));
            }

            try
            {
                lines.Add(SaleLine.Create(
                    lineNumber,
                    medicine.Id,
                    medicine.DisplayName,
                    line.Quantity,
                    line.UnitPrice ?? PriceOfFirst(allocations, candidates),
                    WeightedUnitCost(allocations, candidates),
                    line.DiscountAmount,
                    line.TaxRatePercent ?? medicine.TaxRatePercent,
                    allocations));

                lineNumber++;
            }
            catch (DomainException ex)
            {
                blockers.Add(ex.Message);
                continue;
            }

            // An earlier line may already have taken part of a batch, so the promise is recorded for the
            // availability the next line sees. Nothing is written to the database until the whole basket
            // has been priced and accepted.
            foreach (var allocation in allocations)
            {
                promised[allocation.BatchId] = promised.GetValueOrDefault(allocation.BatchId) + allocation.Quantity;
            }
        }

        var promises = promised
            .Select(promise => new BatchPromise(stock.First(batch => batch.Id == promise.Key), promise.Value))
            .ToList();

        return new Basket(blockers, lines, promises);

        decimal Remaining(MedicineBatch batch) => batch.QuantityOnHand - promised.GetValueOrDefault(batch.Id);

        IEnumerable<BatchAvailability> Availability(IEnumerable<MedicineBatch> batches) =>
            batches.Select(batch => new BatchAvailability(
                batch.Id, batch.BatchNumber, batch.ExpiryDate, Remaining(batch), batch.Status));
    }

    /// <summary>
    /// Price charged when the operator did not type one: the selling price of the batch the customer is
    /// handed first. A line that spans batches priced differently therefore takes the first batch's
    /// price, and the operator can override it on the line.
    /// </summary>
    private static decimal PriceOfFirst(
        IReadOnlyList<BatchAllocation> allocations,
        IReadOnlyList<MedicineBatch> candidates)
    {
        var first = candidates.First(batch => batch.Id == allocations[0].BatchId);

        return Money.Round(first.SellingPrice);
    }

    /// <summary>
    /// Cost per unit of the stock actually consumed: the quantity-weighted average of the batches in the
    /// allocation, rounded once. Kept for margin reporting, never used for pricing.
    /// </summary>
    private static decimal WeightedUnitCost(
        IReadOnlyList<BatchAllocation> allocations,
        IReadOnlyList<MedicineBatch> candidates)
    {
        var cost = 0m;
        var quantity = 0m;

        foreach (var allocation in allocations)
        {
            var batch = candidates.First(candidate => candidate.Id == allocation.BatchId);
            cost += allocation.Quantity * batch.PurchaseCost;
            quantity += allocation.Quantity;
        }

        return quantity == 0m ? 0m : Money.Round(cost / quantity);
    }

    /// <summary>
    /// Runs the checkout as a single unit of work, repeating the whole thing when a competing till changed
    /// the stock this basket was priced against. Repeating is safe because a failed attempt commits nothing:
    /// the basket is re-read and re-priced from scratch, and the invoice number that attempt had already
    /// taken is simply left behind as a gap.
    /// </summary>
    private Task<OperationResult<int>> ExecuteUnitOfWorkAsync(
        MediStockDbContext db,
        Func<Task<OperationResult<int>>> unitOfWork,
        CancellationToken cancellationToken)
        => UnitOfWorkRetry.ExecuteAsync(
            db,
            unitOfWork,
            "Another till sold the same stock while you were ringing this basket up, so the sale was not "
            + "completed and nothing was charged. Check the basket against current stock and try again.",
            _logger,
            cancellationToken);

    private static string Reference(int saleId) => $"SALE-{saleId}";

    /// <summary>
    /// The payments of a basket, for the audit detail. Cash amounts only: the reference a card payment
    /// carries can be a masked number, and the trail has no business holding one.
    /// </summary>
    private static string DescribePayments(SaleDraft request)
        => request.Payments.Count == 0
            ? "no payment recorded"
            : string.Join(
                ", ",
                request.Payments.Select(payment =>
                    string.Format(CultureInfo.InvariantCulture, "{0} {1:0.00}", payment.Method, payment.Amount)));

    /// <summary>
    /// The name a stock movement is signed with, and the account it belongs to when that name really is the
    /// signed-in one. The till is a free-text box, so the two can differ; the trail records what was typed
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

    /// <summary>Loads everything an invoice and its receipt need.</summary>
    private static IQueryable<Sale> Query(MediStockDbContext db) => Load(db.Sales);

    // Split because an invoice carries three collections: one query per collection avoids returning
    // lines x payments x returns rows, which is wasteful for a single invoice and worse for a list of
    // them. Returns are loaded because the screen must show what is left to bring back without a
    // second round trip per invoice.
    private static IQueryable<Sale> Load(IQueryable<Sale> source) => source
        .Include(sale => sale.Lines)
        .ThenInclude(line => line.Allocations)
        .Include(sale => sale.Payments)
        .Include(sale => sale.Returns)
        .ThenInclude(salesReturn => salesReturn.Lines)
        .AsSplitQuery();

    private static SaleDto Project(Sale sale) => new()
    {
        Id = sale.Id,
        InvoiceNumber = sale.InvoiceNumber,
        BusinessDate = sale.BusinessDate,
        SoldAtUtc = sale.SoldAtUtc,
        SoldBy = sale.SoldBy,
        CustomerName = sale.CustomerName,
        CustomerPhone = sale.CustomerPhone,
        Notes = sale.Notes,
        Status = sale.Status,
        SubTotal = sale.SubTotal,
        DiscountTotal = sale.DiscountTotal,
        TaxableTotal = sale.TaxableTotal,
        TaxTotal = sale.TaxTotal,
        Total = sale.Total,
        CostTotal = sale.CostTotal,
        CreatedAtUtc = sale.CreatedAtUtc,
        CancelledAtUtc = sale.CancelledAtUtc,
        CancelledBy = sale.CancelledBy,
        CancellationReason = sale.CancellationReason,
        ReturnedTotal = sale.RefundedTotal,
        Lines =
        [
            .. sale.Lines
                .OrderBy(line => line.LineNumber)
                .Select(Project),
        ],
        Payments =
        [
            .. sale.Payments
                .OrderBy(payment => payment.Id)
                .Select(payment => new PaymentDto(
                    payment.Method, payment.Amount, payment.Reference, payment.PaidAtUtc)),
        ],
        Returns =
        [
            .. sale.Returns
                .OrderBy(salesReturn => salesReturn.BusinessDate)
                .ThenBy(salesReturn => salesReturn.Id)
                .Select(SalesReturnProjection.Summarise),
        ],
    };

    private static SaleLineDto Project(SaleLine line) => new()
    {
        LineNumber = line.LineNumber,
        MedicineId = line.MedicineId,
        MedicineName = line.MedicineName,
        Quantity = line.Quantity,
        UnitPrice = line.UnitPrice,
        UnitCost = line.UnitCost,
        GrossAmount = line.GrossAmount,
        DiscountAmount = line.DiscountAmount,
        TaxableAmount = line.TaxableAmount,
        TaxRatePercent = line.TaxRatePercent,
        TaxAmount = line.TaxAmount,
        LineTotal = line.LineTotal,
        LineCost = line.LineCost,
        ReturnedQuantity = line.ReturnedQuantity,
        ReturnedAmount = line.ReturnedAmount,
        Allocations =
        [
            .. line.Allocations
                .OrderBy(allocation => allocation.ExpiryDate)
                .ThenBy(allocation => allocation.MedicineBatchId)
                .Select(allocation => new SaleAllocationDto
                {
                    MedicineBatchId = allocation.MedicineBatchId,
                    BatchNumber = allocation.BatchNumber,
                    ExpiryDate = allocation.ExpiryDate,
                    Quantity = allocation.Quantity,
                    ReturnedQuantity = allocation.ReturnedQuantity,
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

        if (!SqlFailure.TryClassify(exception, out var kind, out var sql))
        {
            return false;
        }

        if (kind == SqlFailureKind.Duplicate)
        {
            message = SqlFailure.NamesAtLeastOne(sql, "UX_Sales_InvoiceNumber")
                ? "That invoice number has already been used. The sale was not saved; try again."
                : "A record with these details already exists.";

            return true;
        }

        message = SqlFailure.NamesAtLeastOne(sql, "CK_SaleLines", "CK_Sales")
            ? "The invoice failed a database rule on its amounts, so it was not saved."
            : "The sale was rejected by a database rule. Check the basket and try again.";

        return true;
    }

    /// <summary>A priced basket, the reasons it cannot be sold, and the stock it would consume.</summary>
    private sealed record Basket(
        IReadOnlyList<string> Blockers,
        IReadOnlyList<SaleLine> Lines,
        IReadOnlyList<BatchPromise> Promises);

    private sealed record BatchPromise(MedicineBatch Batch, decimal Quantity);
}
