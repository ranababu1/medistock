using System.Text.RegularExpressions;
using MediStock.Application.Catalogue;
using MediStock.Application.Identity;
using MediStock.Application.Inventory;
using MediStock.Application.Purchasing;
using MediStock.Application.Suppliers;
using MediStock.Domain.Catalogue;
using MediStock.Domain.Identity;
using MediStock.Domain.Inventory;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Suppliers;

/// <summary>
/// Supplier returns against a real SQL Server database. A return is a credit document, not a bin: the
/// supplier is re-read, the batches are re-read, the return number is issued and every line takes its
/// quantity off the shelf in one transaction - or the shelf never hears about it at all. These tests hold
/// that line from both sides: the document and the stock ledger must agree after a success, and neither
/// may have changed after a refusal.
/// </summary>
[Collection(TestDatabaseCollection.Name)]
public class SupplierReturnCatalogTests
{
    private const string Operator = "integration-test";

    private static DateOnly Today => BusinessTime.Today;

    private readonly TestDatabaseFixture _fixture;

    public SupplierReturnCatalogTests(TestDatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The happy path proves the whole contract at once: the number the screen previewed is the number
    /// the return carries, the shelf lost exactly what the document says, the movement ledger says the
    /// same thing under the return's reference, the audit entry explains the credit the supplier owes,
    /// and the cached balance still reconciles with the ledger.
    /// </summary>
    [Fact]
    public async Task Recording_a_return_credits_the_supplier_and_takes_the_stock_off_the_shelf()
    {
        var batch = $"B-{Unique()}";

        var returnNumber = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Supplier return test {Unique()}");
            var (supplierId, batchId) = await ReceiveAsync(
                services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var catalog = services.GetRequiredService<ISupplierReturnCatalog>();
            var preview = await catalog.PreviewReturnNumberAsync();

            var recorded = await catalog.RecordAsync(new SupplierReturnDraft
            {
                SupplierId = supplierId,
                ReturnedBy = Operator,
                Reason = "Two strips short-dated and the rest damaged in the carton.",
                SupplierCreditNoteNumber = $"CN-{Unique()}",
                Lines = [new SupplierReturnLineDraft { MedicineBatchId = batchId, Quantity = 4m }],
            });

            Assert.True(recorded.Succeeded, recorded.Message);

            var supplierReturn = recorded.Value!;
            Assert.Matches(new Regex(@"^SRET-\d{4}-\d{6}$"), supplierReturn.ReturnNumber);
            Assert.Equal(preview, supplierReturn.ReturnNumber);
            Assert.Equal(Today, supplierReturn.BusinessDate);
            Assert.Equal(50.00m, supplierReturn.TotalCostValue);

            var line = Assert.Single(supplierReturn.Lines);
            Assert.Equal(batchId, line.MedicineBatchId);
            Assert.Equal(batch, line.BatchNumber);
            Assert.Equal(4m, line.Quantity);
            Assert.Equal(12.50m, line.UnitCost);
            Assert.Equal(50.00m, line.CostValue);

            // The shelf lost the quantity in the same unit of work that wrote the document.
            var shelf = await BatchAsync(services, batch);
            Assert.Equal(6m, shelf.QuantityOnHand);

            var movement = Assert.Single(
                await MovementsAsync(services, medicineId),
                candidate => candidate.MovementType == StockMovementType.SupplierReturn);

            Assert.Equal(-4m, movement.QuantityDelta);
            Assert.Equal(6m, movement.QuantityAfter);
            Assert.Equal($"SRET-{supplierReturn.Id}", movement.Reference);

            var audit = Assert.Single(await AuditAsync(services, AuditAction.SupplierReturnRecorded, supplierReturn.ReturnNumber));
            Assert.Equal("SupplierReturn", audit.EntityType);
            Assert.Equal(supplierReturn.ReturnNumber, audit.EntityId);
            Assert.Contains("credit expected 50.00 at cost", audit.Detail);

            // What the detail view reads is what was committed, not what the screen remembered.
            var reloaded = await catalog.FindAsync(supplierReturn.Id);
            Assert.NotNull(reloaded);
            Assert.Equal(supplierReturn.ReturnNumber, reloaded!.ReturnNumber);
            Assert.Equal(supplierId, reloaded.SupplierId);
            Assert.Equal(4m, Assert.Single(reloaded.Lines).Quantity);

            var byCreditNote = await catalog.SearchAsync(new SupplierReturnQuery { Term = supplierReturn.SupplierCreditNoteNumber });
            var summary = Assert.Single(byCreditNote);
            Assert.Equal(supplierReturn.Id, summary.Id);
            Assert.Equal(4m, summary.ReturnedQuantity);
            Assert.Equal(50.00m, summary.TotalCostValue);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);

            return supplierReturn.ReturnNumber;
        });

        Assert.Matches(new Regex(@"^SRET-\d{4}-\d{6}$"), returnNumber);
    }

    [Fact]
    public async Task Stock_can_only_go_back_to_the_supplier_that_sold_it()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Supplier return test {Unique()}");
            var (boughtFromId, batchId) = await ReceiveAsync(
                services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var other = await services.GetRequiredService<ISupplierCatalog>().CreateAsync(
                new SupplierDraft { Name = $"Unrelated supplier {Unique()}" });
            Assert.True(other.Succeeded, other.Message);
            var otherName = other.Value!.Name;

            var (returns, lines, movements) = await CountsAsync(services);

            var recorded = await services.GetRequiredService<ISupplierReturnCatalog>().RecordAsync(
                new SupplierReturnDraft
                {
                    SupplierId = other.Value.Id,
                    ReturnedBy = Operator,
                    Reason = "Trying to return stock to the wrong supplier.",
                    Lines = [new SupplierReturnLineDraft { MedicineBatchId = batchId, Quantity = 4m }],
                });

            Assert.False(recorded.Succeeded);
            Assert.Equal(
                $"Batch '{batch}' was not bought from {otherName}, so it cannot be returned to them.",
                recorded.Message);

            var after = await CountsAsync(services);
            Assert.Equal(returns, after.Returns);
            Assert.Equal(lines, after.Lines);
            Assert.Equal(movements, after.Movements);
            Assert.Equal(10m, (await BatchAsync(services, batch)).QuantityOnHand);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);
        });
    }
    [Fact]
    public async Task A_return_beyond_what_the_batch_holds_is_refused_and_changes_nothing()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Supplier return test {Unique()}");
            var (supplierId, batchId) = await ReceiveAsync(
                services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var (returns, lines, movements) = await CountsAsync(services);

            var recorded = await services.GetRequiredService<ISupplierReturnCatalog>().RecordAsync(
                new SupplierReturnDraft
                {
                    SupplierId = supplierId,
                    ReturnedBy = Operator,
                    Reason = "Asking for more than the shelf holds.",
                    Lines = [new SupplierReturnLineDraft { MedicineBatchId = batchId, Quantity = 11m }],
                });

            Assert.False(recorded.Succeeded);
            Assert.Equal(
                $"Batch '{batch}' is holding 10, so it cannot return 11. Reduce the quantity or write the "
                + "extra off.",
                recorded.Message);

            var after = await CountsAsync(services);
            Assert.Equal(returns, after.Returns);
            Assert.Equal(lines, after.Lines);
            Assert.Equal(movements, after.Movements);
            Assert.Equal(10m, (await BatchAsync(services, batch)).QuantityOnHand);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);
        });
    }

    /// <summary>
    /// Two lines can name the same batch, and the shelf only sees one batch - so the lines are counted
    /// together before anything moves. Together they ask for more than the batch holds and are refused;
    /// alone the same quantity is fine.
    /// </summary>
    [Fact]
    public async Task Lines_naming_the_same_batch_are_counted_together()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Supplier return test {Unique()}");
            var (supplierId, batchId) = await ReceiveAsync(
                services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var catalog = services.GetRequiredService<ISupplierReturnCatalog>();

            var (returns, lines, movements) = await CountsAsync(services);

            var together = await catalog.RecordAsync(new SupplierReturnDraft
            {
                SupplierId = supplierId,
                ReturnedBy = Operator,
                Reason = "Two lines against one batch, asking for more than it holds.",
                Lines =
                [
                    new SupplierReturnLineDraft { MedicineBatchId = batchId, Quantity = 6m },
                    new SupplierReturnLineDraft { MedicineBatchId = batchId, Quantity = 6m },
                ],
            });

            Assert.False(together.Succeeded);
            Assert.Equal(
                $"Batch '{batch}' is holding 10, so it cannot return 12. Reduce the quantity or write the "
                + "extra off.",
                together.Message);

            var after = await CountsAsync(services);
            Assert.Equal(returns, after.Returns);
            Assert.Equal(lines, after.Lines);
            Assert.Equal(movements, after.Movements);

            var alone = await catalog.RecordAsync(new SupplierReturnDraft
            {
                SupplierId = supplierId,
                ReturnedBy = Operator,
                Reason = "Six units short-dated, sent back for credit.",
                Lines = [new SupplierReturnLineDraft { MedicineBatchId = batchId, Quantity = 6m }],
            });

            Assert.True(alone.Succeeded, alone.Message);
            Assert.Equal(75.00m, alone.Value!.TotalCostValue);
            Assert.Equal(4m, (await BatchAsync(services, batch)).QuantityOnHand);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);

            return alone.Value.ReturnNumber;
        });

        Assert.Matches(new Regex(@"^SRET-\d{4}-\d{6}$"), outcome);
    }

    [Fact]
    public async Task A_recalled_batch_cannot_go_back_to_the_supplier()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Supplier return test {Unique()}");
            var (supplierId, batchId) = await ReceiveAsync(
                services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var quarantined = await services.GetRequiredService<IInventoryOperations>().QuarantineAsync(
                new StockQuarantineDraft
                {
                    MedicineBatchId = batchId,
                    IsRecall = true,
                    Reason = "Recall notice RN-88.",
                });
            Assert.True(quarantined.Succeeded, quarantined.Message);

            var (returns, lines, movements) = await CountsAsync(services);

            var recorded = await services.GetRequiredService<ISupplierReturnCatalog>().RecordAsync(
                new SupplierReturnDraft
                {
                    SupplierId = supplierId,
                    ReturnedBy = Operator,
                    Reason = "Trying to send a recalled batch back.",
                    Lines = [new SupplierReturnLineDraft { MedicineBatchId = batchId, Quantity = 4m }],
                });

            Assert.False(recorded.Succeeded);
            Assert.Equal(
                $"Batch '{batch}' is recalled and cannot be returned to the supplier. Dispose of it "
                + "through the write-off workflow instead.",
                recorded.Message);

            var after = await CountsAsync(services);
            Assert.Equal(returns, after.Returns);
            Assert.Equal(lines, after.Lines);
            Assert.Equal(movements, after.Movements);
            Assert.Equal(10m, (await BatchAsync(services, batch)).QuantityOnHand);
        });
    }

    [Fact]
    public async Task A_return_needs_lines_a_supplier_and_a_reason()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Supplier return test {Unique()}");
            var (supplierId, batchId) = await ReceiveAsync(
                services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var catalog = services.GetRequiredService<ISupplierReturnCatalog>();

            var (returns, lines, movements) = await CountsAsync(services);

            var noLines = await catalog.RecordAsync(new SupplierReturnDraft
            {
                SupplierId = supplierId,
                ReturnedBy = Operator,
                Reason = "No lines at all.",
                Lines = [],
            });
            Assert.False(noLines.Succeeded);
            Assert.Equal(
                "A supplier return must send back at least one quantity. Add a line and try again.",
                noLines.Message);

            var noSupplier = await catalog.RecordAsync(new SupplierReturnDraft
            {
                ReturnedBy = Operator,
                Reason = "No supplier chosen.",
                Lines = [new SupplierReturnLineDraft { MedicineBatchId = batchId, Quantity = 4m }],
            });
            Assert.False(noSupplier.Succeeded);
            Assert.Equal(
                "A supplier return must name the supplier the goods go back to. Choose the supplier first.",
                noSupplier.Message);

            var unknownSupplier = await catalog.RecordAsync(new SupplierReturnDraft
            {
                SupplierId = 999_999,
                ReturnedBy = Operator,
                Reason = "A supplier that does not exist.",
                Lines = [new SupplierReturnLineDraft { MedicineBatchId = batchId, Quantity = 4m }],
            });
            Assert.False(unknownSupplier.Succeeded);
            Assert.Equal(
                "That supplier could not be found, so nothing was returned. Choose the supplier the goods "
                + "were bought from.",
                unknownSupplier.Message);

            var noReason = await catalog.RecordAsync(new SupplierReturnDraft
            {
                SupplierId = supplierId,
                ReturnedBy = Operator,
                Reason = "   ",
                Lines = [new SupplierReturnLineDraft { MedicineBatchId = batchId, Quantity = 4m }],
            });
            Assert.False(noReason.Succeeded);
            Assert.Equal("Reason is required.", noReason.Message);

            var unknownBatch = await catalog.RecordAsync(new SupplierReturnDraft
            {
                SupplierId = supplierId,
                ReturnedBy = Operator,
                Reason = "A batch that does not exist.",
                Lines = [new SupplierReturnLineDraft { MedicineBatchId = 999_999, Quantity = 4m }],
            });
            Assert.False(unknownBatch.Succeeded);
            Assert.Equal(
                "Batch 999999 could not be loaded, so it cannot be returned.",
                unknownBatch.Message);

            var after = await CountsAsync(services);
            Assert.Equal(returns, after.Returns);
            Assert.Equal(lines, after.Lines);
            Assert.Equal(movements, after.Movements);
            Assert.Equal(10m, (await BatchAsync(services, batch)).QuantityOnHand);
        });
    }

    /// <summary>
    /// The list the Returns screen reads: a return is looked up by its own number, by the credit note the
    /// supplier issued, by the date it counts towards, or by the supplier it went back to - whichever the
    /// operator happens to be holding.
    /// </summary>
    [Fact]
    public async Task The_returns_list_finds_returns_by_number_note_date_and_supplier()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Supplier return test {Unique()}");
            var (supplierId, batchId) = await ReceiveAsync(
                services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var catalog = services.GetRequiredService<ISupplierReturnCatalog>();

            var older = await catalog.RecordAsync(new SupplierReturnDraft
            {
                SupplierId = supplierId,
                ReturnedBy = Operator,
                Reason = "Short-dated stock returned last week.",
                SupplierCreditNoteNumber = $"CN-{Unique()}",
                BusinessDate = Today.AddDays(-5),
                Lines = [new SupplierReturnLineDraft { MedicineBatchId = batchId, Quantity = 2m }],
            });
            Assert.True(older.Succeeded, older.Message);

            var newer = await catalog.RecordAsync(new SupplierReturnDraft
            {
                SupplierId = supplierId,
                ReturnedBy = Operator,
                Reason = "Damaged stock returned today.",
                Lines = [new SupplierReturnLineDraft { MedicineBatchId = batchId, Quantity = 3m }],
            });
            Assert.True(newer.Succeeded, newer.Message);

            var byNumber = await catalog.SearchAsync(
                new SupplierReturnQuery { Term = older.Value!.ReturnNumber });
            var numberRow = Assert.Single(byNumber);
            Assert.Equal(older.Value.Id, numberRow.Id);

            var byCreditNote = await catalog.SearchAsync(
                new SupplierReturnQuery { Term = older.Value.SupplierCreditNoteNumber });
            var noteRow = Assert.Single(byCreditNote);
            Assert.Equal(older.Value.Id, noteRow.Id);

            var thisWeekOnly = await catalog.SearchAsync(
                new SupplierReturnQuery { SupplierId = supplierId, FromBusinessDate = Today });
            var thisWeek = Assert.Single(thisWeekOnly);
            Assert.Equal(newer.Value!.Id, thisWeek.Id);
            Assert.Equal(3m, thisWeek.ReturnedQuantity);

            var lastWeekOnly = await catalog.SearchAsync(
                new SupplierReturnQuery { SupplierId = supplierId, ToBusinessDate = Today.AddDays(-1) });
            var lastWeek = Assert.Single(lastWeekOnly);
            Assert.Equal(older.Value.Id, lastWeek.Id);
            Assert.Equal(Today.AddDays(-5), lastWeek.BusinessDate);
            Assert.Equal(2m, lastWeek.ReturnedQuantity);

            // Newest business date first, whatever else the list is filtering by.
            var both = await catalog.SearchAsync(new SupplierReturnQuery { SupplierId = supplierId });
            Assert.Equal([newer.Value.Id, older.Value.Id], both.Select(row => row.Id));

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);
        });
    }

    private async Task<int> SeedMedicineAsync(
        IServiceProvider services,
        string displayName,
        decimal taxRatePercent = 0m)
    {
        var medicine = await services.GetRequiredService<IMedicineCatalog>().CreateAsync(new MedicineDraft
        {
            DisplayName = displayName,
            DosageForm = DosageForm.Tablet,
            Strength = "500 mg",
            PackDescription = "Strip of 10 tablets",
            TaxRatePercent = taxRatePercent,
        });

        Assert.True(medicine.Succeeded, medicine.Message);
        return medicine.Value!.Id;
    }

    /// <summary>
    /// Puts stock on the shelf through the real purchasing workflow, so the batch the return names is
    /// exactly the batch a pharmacy would have: bought from a named supplier, with its receipt movement and
    /// its cost. The supplier is returned with the batch, because a return can only credit that supplier.
    /// </summary>
    private async Task<(int SupplierId, int BatchId)> ReceiveAsync(
        IServiceProvider services,
        int medicineId,
        (string BatchNumber, DateOnly ExpiryDate, decimal Quantity, decimal UnitCost, decimal SellingPrice) line)
    {
        var supplier = await services.GetRequiredService<ISupplierCatalog>().CreateAsync(
            new SupplierDraft { Name = $"Supplier return test {Unique()}" });
        Assert.True(supplier.Succeeded, supplier.Message);

        var catalog = services.GetRequiredService<IPurchaseCatalog>();

        var created = await catalog.CreateAsync(new PurchaseDraft
        {
            SupplierId = supplier.Value!.Id,
            InvoiceNumber = $"SRET-{Unique()}",
            InvoiceDate = Today,
            Lines =
            [
                new PurchaseLineDraft
                {
                    MedicineId = medicineId,
                    BatchNumber = line.BatchNumber,
                    ExpiryDate = line.ExpiryDate,
                    Quantity = line.Quantity,
                    UnitCost = line.UnitCost,
                    SellingPrice = line.SellingPrice,
                },
            ],
        });

        Assert.True(created.Succeeded, created.Message);

        var received = await catalog.ReceiveAsync(created.Value!.Id, Today, Operator);
        Assert.True(received.Succeeded, received.Message);

        var purchase = await catalog.FindAsync(created.Value.Id);
        var receivedLine = Assert.Single(purchase!.Lines);
        Assert.NotNull(receivedLine.MedicineBatchId);
        return (supplier.Value.Id, receivedLine.MedicineBatchId!.Value);
    }

    private static async Task<BatchStockDto> BatchAsync(IServiceProvider services, string batchNumber)
    {
        var batches = await services.GetRequiredService<IStockQueries>().ListBatchesAsync(
            new BatchStockQuery { Term = batchNumber, InStockOnly = false });

        return Assert.Single(batches);
    }

    private static Task<IReadOnlyList<StockMovementDto>> MovementsAsync(IServiceProvider services, int medicineId)
        => services.GetRequiredService<IStockQueries>()
            .ListMovementsAsync(new StockMovementQuery { MedicineId = medicineId });

    /// <summary>The audit rows about one return, which the credit it promises has to be traceable through.</summary>
    private static Task<IReadOnlyList<AuditEntryDto>> AuditAsync(
        IServiceProvider services,
        AuditAction action,
        string returnNumber)
        => services.GetRequiredService<IAuditTrail>().SearchAsync(
            new AuditQuery { Action = action, EntityType = "SupplierReturn", Term = returnNumber, Take = 5 });

    /// <summary>Counts the rows a return would have had to write, to prove a refused one wrote none.</summary>
    private static async Task<(int Returns, int Lines, int Movements)> CountsAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<MediStockDbContext>();

        return (
            await db.SupplierReturns.CountAsync(),
            await db.SupplierReturnLines.CountAsync(),
            await db.StockMovements.CountAsync());
    }

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];
}