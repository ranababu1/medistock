using MediStock.Application.Catalogue;
using MediStock.Application.Common;
using MediStock.Application.Inventory;
using MediStock.Application.Purchasing;
using MediStock.Application.Suppliers;
using MediStock.Domain.Catalogue;
using MediStock.Domain.Inventory;
using MediStock.Domain.Purchasing;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Purchasing;

/// <summary>
/// The receiving workflow against a real SQL Server database. Receiving is the only operation that
/// creates stock from outside the pharmacy, so these tests are mostly about what must NOT happen:
/// stock must not appear twice, must not appear at all when the receive fails, and must not be
/// quietly erased by a correction.
/// </summary>
[Collection(TestDatabaseCollection.Name)]
public class PurchaseCatalogTests
{
    private const string Operator = "integration-test";

    private static DateOnly Today => BusinessTime.Today;

    private const int ExpiryYears = 2;

    private static DateOnly Expiry => Today.AddYears(ExpiryYears);

    private readonly TestDatabaseFixture _fixture;

    public PurchaseCatalogTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task Receiving_creates_one_batch_and_one_receipt_movement_per_line()
    {
        var batchA = $"B-{Unique()}";
        var batchB = $"B-{Unique()}";

        var result = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var created = await catalog.CreateAsync(Draft(supplierId, medicineId, batchA, batchB));
            Assert.True(created.Succeeded, created.Message);

            var received = await catalog.ReceiveAsync(created.Value!.Id, Today, Operator);
            Assert.True(received.Succeeded, received.Message);

            return new
            {
                Purchase = await catalog.FindAsync(created.Value.Id),
                BatchA = await FindBatchAsync(services, batchA),
                MovementsA = await ListMovementsAsync(services, medicineId),
            };
        });

        var purchase = result.Purchase!;
        Assert.Equal(PurchaseStatus.Received, purchase.Status);
        Assert.Equal(Today, purchase.ReceivedOn);
        Assert.Equal(Operator, purchase.ReceivedBy);
        Assert.Equal("SUN-1001", purchase.InvoiceNumber);
        Assert.Equal(245.00m, purchase.TotalValue);
        Assert.All(purchase.Lines, line =>
        {
            Assert.NotNull(line.MedicineBatchId);
            Assert.Equal(line.Quantity, line.QuantityReceived);
        });

        // 10 x 12.50 + 4 x 30.00
        Assert.Equal(125.00m, purchase.Lines[0].LineTotal);
        Assert.Equal(120.00m, purchase.Lines[1].LineTotal);

        var batch = result.BatchA;
        Assert.Equal(10m, batch.QuantityReceived);
        Assert.Equal(10m, batch.QuantityOnHand);
        Assert.Equal(12.50m, batch.PurchaseCost);
        Assert.Equal(18.00m, batch.SellingPrice);
        Assert.Equal(Today, batch.ReceivedOn);
        Assert.Equal("SUN-1001", batch.SupplierInvoiceNumber);
        Assert.True(batch.IsSellable);
        Assert.True(batch.DaysUntilExpiry > 0);

        var receipt = Assert.Single(result.MovementsA, m => m.MedicineBatchId == batch.BatchId);
        Assert.Equal(StockMovementType.Receipt, receipt.MovementType);
        Assert.Equal(10m, receipt.QuantityDelta);
        Assert.Equal(10m, receipt.QuantityAfter);
        Assert.Equal(Today, receipt.BusinessDate);
        Assert.Equal(Operator, receipt.PerformedBy);
    }

    [Fact]
    public async Task Receiving_the_same_purchase_twice_is_refused_and_changes_nothing()
    {
        var batch = $"B-{Unique()}";

        var (failure, batchesBefore, batchesAfter, movementsBefore, movementsAfter, status) =
            await _fixture.InScopeAsync(async services =>
            {
                var (supplierId, medicineId) = await SeedPartiesAsync(services);
                var catalog = services.GetRequiredService<IPurchaseCatalog>();

                var created = await catalog.CreateAsync(Draft(supplierId, medicineId, batch));
                Assert.True(created.Succeeded, created.Message);

                var first = await catalog.ReceiveAsync(created.Value!.Id, Today, Operator);
                Assert.True(first.Succeeded, first.Message);

                var before = await FindBatchAsync(services, batch);
                var movementsBeforeCount = (await ListMovementsAsync(services, medicineId)).Count;

                var second = await catalog.ReceiveAsync(created.Value.Id, Today, Operator);

                return (
                    second,
                    before.QuantityOnHand,
                    (await FindBatchAsync(services, batch)).QuantityOnHand,
                    movementsBeforeCount,
                    (await ListMovementsAsync(services, medicineId)).Count,
                    (await catalog.FindAsync(created.Value.Id))!.Status);
            });

        Assert.False(failure.Succeeded);
        Assert.Contains("Only a draft purchase", failure.Message, StringComparison.Ordinal);
        Assert.Equal(batchesBefore, batchesAfter);
        Assert.Equal(movementsBefore, movementsAfter);
        Assert.Equal(PurchaseStatus.Received, status);
    }

    /// <summary>
    /// Two operators pressing Receive at the same moment must not take the same invoice into stock
    /// twice. Both calls run on their own database context, so this is a real race rather than two
    /// calls queued behind a shared context.
    /// </summary>
    /// <remarks>
    /// The header's rowversion is what makes this hold whatever the interleaving is: if the loser reads
    /// the purchase before the winner commits, its update matches no row; if it reads after, the
    /// aggregate refuses because the purchase is no longer a draft. An unhandled exception therefore
    /// fails this test rather than being counted as the loser.
    /// </remarks>
    [Fact]
    public async Task Two_receives_racing_for_the_same_invoice_can_only_take_the_stock_in_once()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var created = await catalog.CreateAsync(Draft(supplierId, medicineId, batch));
            Assert.True(created.Succeeded, created.Message);
            var purchaseId = created.Value!.Id;

            var attempts = await Task.WhenAll(
                catalog.ReceiveAsync(purchaseId, Today, Operator),
                catalog.ReceiveAsync(purchaseId, Today, "second-operator"));

            var queries = services.GetRequiredService<IStockQueries>();

            return (
                Wins: attempts.Count(attempt => attempt.Succeeded),
                Refusals: attempts.Where(attempt => !attempt.Succeeded).Select(attempt => attempt.Message).ToList(),
                Batches: await queries.ListBatchesAsync(new BatchStockQuery { Term = batch, InStockOnly = false }),
                Movements: await ListMovementsAsync(services, medicineId),
                Purchase: await catalog.FindAsync(purchaseId),
                Reconciliation: await queries.CheckReconciliationAsync());
        });

        Assert.Equal(1, outcome.Wins);
        Assert.All(outcome.Refusals, message => Assert.False(string.IsNullOrWhiteSpace(message)));

        var batchRow = Assert.Single(outcome.Batches);
        Assert.Equal(10m, batchRow.QuantityOnHand);
        Assert.Equal(PurchaseStatus.Received, outcome.Purchase!.Status);

        var ledger = outcome.Movements
            .Where(m => m.MedicineBatchId == batchRow.BatchId)
            .Sum(m => m.QuantityDelta);
        Assert.Equal(10m, ledger);
        Assert.True(outcome.Reconciliation.IsReconciled);
    }

    /// <summary>
    /// The same race one step earlier in the lifecycle, and the one a rowversion is really for: an
    /// operator opens the purchase to review it, somebody else receives the goods, and the first
    /// operator's copy is now stale. Writing that stale copy back would put the delivery into stock a
    /// second time. The database must refuse the conditional update.
    /// </summary>
    [Fact]
    public async Task A_stale_copy_of_a_received_purchase_cannot_be_written_back()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var created = await catalog.CreateAsync(Draft(supplierId, medicineId, batch));
            Assert.True(created.Succeeded, created.Message);
            var purchaseId = created.Value!.Id;

            var stale = services.GetRequiredService<MediStockDbContext>();
            var stalePurchase = await stale.Purchases
                .Include(p => p.Lines)
                .SingleAsync(p => p.Id == purchaseId);

            // A different context receives the delivery and commits.
            var received = await catalog.ReceiveAsync(purchaseId, Today, Operator);
            Assert.True(received.Succeeded, received.Message);

            stalePurchase.Receive(Today, "late-operator", DateTimeOffset.UtcNow);

            var failure = await Record.ExceptionAsync(() => stale.SaveChangesAsync());
            var queries = services.GetRequiredService<IStockQueries>();

            return (
                Failure: failure,
                Batches: await queries.ListBatchesAsync(new BatchStockQuery { Term = batch, InStockOnly = false }),
                Purchase: await catalog.FindAsync(purchaseId),
                Reconciliation: await queries.CheckReconciliationAsync());
        });

        var conflict = Assert.IsType<DbUpdateConcurrencyException>(outcome.Failure);
        Assert.Contains(conflict.Entries, entry => entry.Entity is Purchase);

        // The stale write left nothing behind: one batch, still fully on the shelf.
        var batchRow = Assert.Single(outcome.Batches);
        Assert.Equal(10m, batchRow.QuantityOnHand);
        Assert.Equal(PurchaseStatus.Received, outcome.Purchase!.Status);
        Assert.Equal(Operator, outcome.Purchase.ReceivedBy);
        Assert.True(outcome.Reconciliation.IsReconciled);
    }

    [Fact]
    public async Task Receiving_stock_that_expires_on_the_receiving_date_is_refused()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var draft = Draft(supplierId, medicineId, batch) with
            {
                Lines = [Line(medicineId, batch, 10m, 12.50m, 18m) with { ExpiryDate = Today }],
            };

            var created = await catalog.CreateAsync(draft);
            Assert.True(created.Succeeded, created.Message);

            return (
                Failure: await catalog.ReceiveAsync(created.Value!.Id, Today, Operator),
                Batch: await FindAnyBatchAsync(services, batch));
        });

        Assert.False(outcome.Failure.Succeeded);
        Assert.Contains("expires on or before", outcome.Failure.Message, StringComparison.Ordinal);
        Assert.Null(outcome.Batch);
    }

    [Fact]
    public async Task Reversing_an_untouched_receipt_takes_the_stock_out_and_keeps_the_ledger_honest()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var created = await catalog.CreateAsync(Draft(supplierId, medicineId, batch));
            Assert.True(created.Succeeded, created.Message);
            Assert.True((await catalog.ReceiveAsync(created.Value!.Id, Today, Operator)).Succeeded);

            var reversed = await catalog.ReverseAsync(created.Value.Id, "damaged in transit", Operator);

            return (
                Reversed: reversed,
                Purchase: await catalog.FindAsync(created.Value.Id),
                Batch: await FindAnyBatchAsync(services, batch),
                Movements: await ListMovementsAsync(services, medicineId),
                Reconciliation: await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync());
        });

        Assert.True(outcome.Reversed.Succeeded, outcome.Reversed.Message);
        Assert.Equal(PurchaseStatus.Reversed, outcome.Purchase!.Status);
        Assert.Equal("damaged in transit", outcome.Purchase.StatusReason);
        Assert.Equal(Operator, outcome.Purchase.ClosedBy);
        Assert.NotNull(outcome.Purchase.ClosedAtUtc);

        // The stock is gone from the shelf, but the history is not: the receipt is still there next to
        // its matching reversal, which is what keeps the ledger equal to the cached balances.
        Assert.NotNull(outcome.Batch);
        Assert.Equal(0m, outcome.Batch!.QuantityOnHand);
        Assert.Equal(BatchStatus.Depleted, outcome.Batch.Status);
        Assert.False(outcome.Batch.IsSellable);
        Assert.Contains(outcome.Movements, m => m.MovementType == StockMovementType.Receipt);
        var reversal = Assert.Single(outcome.Movements, m => m.MovementType == StockMovementType.ReceiptReversal);
        Assert.Equal(-10m, reversal.QuantityDelta);
        Assert.Equal(0m, reversal.QuantityAfter);
        Assert.True(outcome.Reconciliation.IsReconciled);
    }

    /// <summary>
    /// The mirror image of the receive race. Two operators correcting the same receipt at the same
    /// moment must take the stock out once: a second reversal would leave the ledger at minus the
    /// received quantity while the shelf balance reads zero - stock that has evaporated without a
    /// movement to explain it.
    /// </summary>
    [Fact]
    public async Task Two_reversals_racing_for_the_same_receipt_can_only_take_the_stock_out_once()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var created = await catalog.CreateAsync(Draft(supplierId, medicineId, batch));
            Assert.True(created.Succeeded, created.Message);
            var purchaseId = created.Value!.Id;
            Assert.True((await catalog.ReceiveAsync(purchaseId, Today, Operator)).Succeeded);

            var attempts = await Task.WhenAll(
                catalog.ReverseAsync(purchaseId, "delivered in error", Operator),
                catalog.ReverseAsync(purchaseId, "duplicate delivery", "second-operator"));

            var queries = services.GetRequiredService<IStockQueries>();

            return (
                Wins: attempts.Count(attempt => attempt.Succeeded),
                Refusals: attempts.Where(attempt => !attempt.Succeeded).Select(attempt => attempt.Message).ToList(),
                Movements: await ListMovementsAsync(services, medicineId),
                Purchase: await catalog.FindAsync(purchaseId),
                Reconciliation: await queries.CheckReconciliationAsync());
        });

        Assert.Equal(1, outcome.Wins);
        Assert.All(outcome.Refusals, message => Assert.False(string.IsNullOrWhiteSpace(message)));
        Assert.Equal(PurchaseStatus.Reversed, outcome.Purchase!.Status);

        // One receipt, one reversal: the ledger for this medicine is flat and the shelf is empty.
        var reversal = Assert.Single(outcome.Movements, m => m.MovementType == StockMovementType.ReceiptReversal);
        Assert.Equal(-10m, reversal.QuantityDelta);
        Assert.Equal(0m, outcome.Movements.Sum(m => m.QuantityDelta));
        Assert.True(outcome.Reconciliation.IsReconciled);
    }

    [Fact]
    public async Task Reversing_is_refused_once_any_of_the_batch_stock_has_been_sold()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var created = await catalog.CreateAsync(Draft(supplierId, medicineId, batch));
            Assert.True(created.Succeeded, created.Message);
            Assert.True((await catalog.ReceiveAsync(created.Value!.Id, Today, Operator)).Succeeded);

            // Sell two units straight out of the batch, exactly as checkout would.
            var db = services.GetRequiredService<MediStockDbContext>();
            var stored = await db.MedicineBatches.FirstAsync(b => b.BatchNumber == batch);
            stored.DecreaseQuantity(2m);
            db.StockMovements.Add(StockMovement.Record(
                medicineId, stored.Id, StockMovementType.Sale, -2m, stored.QuantityOnHand, Today, Operator,
                reference: "TEST-SALE"));
            await db.SaveChangesAsync();

            return (
                Failure: await catalog.ReverseAsync(created.Value.Id, "wrong invoice", Operator),
                Purchase: await catalog.FindAsync(created.Value.Id),
                Batch: await FindBatchAsync(services, batch));
        });

        Assert.False(outcome.Failure.Succeeded);
        Assert.NotEmpty(outcome.Failure.Message);
        Assert.Equal(PurchaseStatus.Received, outcome.Purchase!.Status);
        Assert.Equal(8m, outcome.Batch.QuantityOnHand);
        Assert.True(outcome.Batch.IsSellable);
    }

    [Fact]
    public async Task Cancelling_is_refused_once_the_goods_have_been_received()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var created = await catalog.CreateAsync(Draft(supplierId, medicineId, batch));
            Assert.True(created.Succeeded, created.Message);
            Assert.True((await catalog.ReceiveAsync(created.Value!.Id, Today, Operator)).Succeeded);

            return (
                Failure: await catalog.CancelAsync(created.Value.Id, "no longer needed", Operator),
                Purchase: await catalog.FindAsync(created.Value.Id));
        });

        Assert.False(outcome.Failure.Succeeded);
        Assert.Equal(PurchaseStatus.Received, outcome.Purchase!.Status);
    }

    [Fact]
    public async Task Cancelling_a_draft_abandons_it_without_creating_any_stock()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var created = await catalog.CreateAsync(Draft(supplierId, medicineId, batch));
            Assert.True(created.Succeeded, created.Message);

            var cancelled = await catalog.CancelAsync(created.Value!.Id, "supplier withdrew the invoice", Operator);

            return (
                Cancelled: cancelled,
                Purchase: await catalog.FindAsync(created.Value.Id),
                Batch: await FindAnyBatchAsync(services, batch));
        });

        Assert.True(outcome.Cancelled.Succeeded, outcome.Cancelled.Message);
        Assert.Equal(PurchaseStatus.Cancelled, outcome.Purchase!.Status);
        Assert.Equal("supplier withdrew the invoice", outcome.Purchase.StatusReason);
        Assert.Equal(Operator, outcome.Purchase.ClosedBy);
        Assert.Null(outcome.Batch);
    }

    [Fact]
    public async Task Cancelling_requires_a_reason()
    {
        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var created = await catalog.CreateAsync(Draft(supplierId, medicineId, $"B-{Unique()}"));

            return (
                Failure: await catalog.CancelAsync(created.Value!.Id, "   ", Operator),
                Purchase: await catalog.FindAsync(created.Value.Id));
        });

        Assert.False(outcome.Failure.Succeeded);
        Assert.Equal(PurchaseStatus.Draft, outcome.Purchase!.Status);
    }

    [Fact]
    public async Task A_likely_duplicate_invoice_can_be_found_before_it_is_received_twice()
    {
        var invoice = $"SUN-{Unique()}";
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var draft = Draft(supplierId, medicineId, batch) with { InvoiceNumber = invoice };
            var created = await catalog.CreateAsync(draft);

            return (
                Created: created.Value!.Id,
                Matches: await catalog.FindByInvoiceAsync(supplierId, invoice),
                ExcludingSelf: await catalog.FindByInvoiceAsync(supplierId, invoice, created.Value.Id),
                OtherSupplier: await catalog.FindByInvoiceAsync(supplierId + 500_000, invoice));
        });

        var match = Assert.Single(outcome.Matches);
        Assert.Equal(outcome.Created, match.Id);
        Assert.Equal(invoice, match.InvoiceNumber);
        Assert.Equal(PurchaseStatus.Draft, match.Status);
        Assert.Equal(125.00m, match.TotalValue);
        Assert.Equal(1, match.LineCount);
        Assert.Empty(outcome.ExcludingSelf);
        Assert.Empty(outcome.OtherSupplier);
    }

    [Fact]
    public async Task Updating_a_draft_replaces_its_lines_and_keeps_the_total_in_step()
    {
        var batchA = $"B-{Unique()}";
        var batchB = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var created = await catalog.CreateAsync(Draft(supplierId, medicineId, batchA));
            Assert.True(created.Succeeded, created.Message);

            var edited = Draft(supplierId, medicineId, batchA, batchB) with { Notes = "revised delivery" };
            var updated = await catalog.UpdateAsync(created.Value!.Id, edited);

            return (Updated: updated, Purchase: await catalog.FindAsync(created.Value.Id));
        });

        Assert.True(outcome.Updated.Succeeded, outcome.Updated.Message);
        var purchase = outcome.Purchase!;
        Assert.Equal("revised delivery", purchase.Notes);
        Assert.Equal(2, purchase.Lines.Count);
        Assert.Equal(245.00m, purchase.TotalValue);
        Assert.Equal(purchase.Lines.Sum(line => line.LineTotal), purchase.TotalValue);
    }

    [Fact]
    public async Task The_same_medicine_and_batch_cannot_appear_twice_on_one_purchase()
    {
        var batch = $"B-{Unique()}";

        var failure = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);

            return await services.GetRequiredService<IPurchaseCatalog>()
                .CreateAsync(Draft(supplierId, medicineId, batch, batch));
        });

        Assert.False(failure.Succeeded);
        Assert.Contains("Combine the quantities", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_blank_invoice_number_is_reported_as_a_domain_rule_violation()
    {
        var failure = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var draft = Draft(supplierId, medicineId, $"B-{Unique()}") with { InvoiceNumber = "   " };

            return await services.GetRequiredService<IPurchaseCatalog>().CreateAsync(draft);
        });

        Assert.False(failure.Succeeded);
        Assert.Contains("Invoice number is required.", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Search_filters_on_supplier_status_and_the_invoice_date()
    {
        var invoice = $"EDH-{Unique()}";
        var batch = $"B-{Unique()}";

        var results = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var created = await catalog.CreateAsync(Draft(supplierId, medicineId, batch) with { InvoiceNumber = invoice });
            Assert.True(created.Succeeded, created.Message);

            var draftOnly = await catalog.SearchAsync(new PurchaseSearchQuery
            {
                SupplierId = supplierId,
                Status = PurchaseStatus.Draft,
            });
            var receivedOnly = await catalog.SearchAsync(new PurchaseSearchQuery
            {
                SupplierId = supplierId,
                Status = PurchaseStatus.Received,
            });
            var byInvoice = await catalog.SearchAsync(new PurchaseSearchQuery { Term = invoice });
            var outsideTheRange = await catalog.SearchAsync(new PurchaseSearchQuery
            {
                SupplierId = supplierId,
                InvoiceDateFrom = Today.AddDays(1),
            });

            return (DraftOnly: draftOnly, ReceivedOnly: receivedOnly, ByInvoice: byInvoice, Outside: outsideTheRange);
        });

        Assert.Single(results.DraftOnly);
        Assert.Empty(results.ReceivedOnly);
        Assert.Single(results.ByInvoice);
        Assert.Equal(invoice, results.ByInvoice[0].InvoiceNumber);
        Assert.Empty(results.Outside);
    }

    [Fact]
    public async Task Receiving_keeps_every_cached_batch_balance_equal_to_its_ledger_sum()
    {
        var batch = $"B-{Unique()}";

        var summary = await _fixture.InScopeAsync(async services =>
        {
            var (supplierId, medicineId) = await SeedPartiesAsync(services);
            var catalog = services.GetRequiredService<IPurchaseCatalog>();

            var created = await catalog.CreateAsync(Draft(supplierId, medicineId, batch));
            Assert.True(created.Succeeded, created.Message);
            Assert.True((await catalog.ReceiveAsync(created.Value!.Id, Today, Operator)).Succeeded);

            return await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync();
        });

        Assert.True(summary.IsReconciled, $"{summary.BatchesMismatched} of {summary.BatchesChecked} batches disagree with the ledger.");
        Assert.Equal(summary.LedgerQuantity, summary.CachedQuantity);
        Assert.True(summary.BatchesChecked > 0);
    }

    private async Task<(int SupplierId, int MedicineId)> SeedPartiesAsync(IServiceProvider services)
    {
        var supplier = await services.GetRequiredService<ISupplierCatalog>().CreateAsync(
            new SupplierDraft { Name = $"Distributor {Unique()}" });
        Assert.True(supplier.Succeeded, supplier.Message);

        var medicine = await services.GetRequiredService<IMedicineCatalog>().CreateAsync(new MedicineDraft
        {
            DisplayName = $"Purchase test {Unique()}",
            DosageForm = DosageForm.Tablet,
            Strength = "500 mg",
            PackDescription = "Strip of 10 tablets",
        });
        Assert.True(medicine.Succeeded, medicine.Message);

        return (supplier.Value!.Id, medicine.Value!.Id);
    }

    private static PurchaseDraft Draft(int supplierId, int medicineId, string firstBatch, string? secondBatch = null)
    {
        var lines = new List<PurchaseLineDraft> { Line(medicineId, firstBatch, 10m, 12.50m, 18m) };
        if (secondBatch is not null)
        {
            lines.Add(Line(medicineId, secondBatch, 4m, 30m, 44m));
        }

        return new PurchaseDraft
        {
            SupplierId = supplierId,
            InvoiceNumber = "SUN-1001",
            InvoiceDate = Today,
            Lines = lines,
        };
    }

    private static PurchaseLineDraft Line(int medicineId, string batchNumber, decimal quantity, decimal unitCost, decimal sellingPrice) => new()
    {
        MedicineId = medicineId,
        BatchNumber = batchNumber,
        ExpiryDate = Expiry,
        Quantity = quantity,
        UnitCost = unitCost,
        SellingPrice = sellingPrice,
    };

    /// <summary>A batch that is still on the shelf, so the assertion also proves it is sellable.</summary>
    private static async Task<BatchStockDto> FindBatchAsync(IServiceProvider services, string batchNumber)
    {
        var batches = await services.GetRequiredService<IStockQueries>().ListBatchesAsync(
            new BatchStockQuery { Term = batchNumber, InStockOnly = true });

        return Assert.Single(batches);
    }

    /// <summary>A batch whether or not any stock is left, used to prove a receive wrote nothing.</summary>
    private static async Task<BatchStockDto?> FindAnyBatchAsync(IServiceProvider services, string batchNumber)
    {
        var batches = await services.GetRequiredService<IStockQueries>().ListBatchesAsync(
            new BatchStockQuery { Term = batchNumber, InStockOnly = false });

        return batches.Count == 0 ? null : Assert.Single(batches);
    }

    private static Task<IReadOnlyList<StockMovementDto>> ListMovementsAsync(IServiceProvider services, int medicineId)
        => services.GetRequiredService<IStockQueries>().ListMovementsAsync(new StockMovementQuery { MedicineId = medicineId });

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];
}
