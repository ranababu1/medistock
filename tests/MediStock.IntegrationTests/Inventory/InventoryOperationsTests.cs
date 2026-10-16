using MediStock.Application.Catalogue;
using MediStock.Application.Identity;
using MediStock.Application.Inventory;
using MediStock.Application.Purchasing;
using MediStock.Application.Sales;
using MediStock.Application.Suppliers;
using MediStock.Domain.Catalogue;
using MediStock.Domain.Identity;
using MediStock.Domain.Inventory;
using MediStock.Domain.Sales;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Inventory;

/// <summary>
/// Manual stock corrections against a real SQL Server database. A correction is the only operation
/// that changes what the ledger claims without a document behind it - no invoice, no purchase, no
/// return - so what it must never do is change the ledger halfway. Every refusal here is measured the
/// same way: the shelf still holds what it held, the movement ledger still says what it said, and the
/// cached balance still reconciles.
/// </summary>
[Collection(TestDatabaseCollection.Name)]
public class InventoryOperationsTests
{
    private const string Operator = "integration-test";

    private static DateOnly Today => BusinessTime.Today;

    private readonly TestDatabaseFixture _fixture;

    public InventoryOperationsTests(TestDatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A found-again quantity is stock the supplier really sent, so an increase counts towards what the
    /// batch received as well as what it holds. The movement records the two the batch now claims, and the
    /// audit entry says why the operator said so.
    /// </summary>
    [Fact]
    public async Task An_adjustment_up_counts_stock_the_supplier_actually_sent()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Adjustment test {Unique()}");
            var (_, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var adjusted = await services.GetRequiredService<IInventoryOperations>().AdjustAsync(
                new StockAdjustmentDraft
                {
                    MedicineBatchId = batchId,
                    IsIncrease = true,
                    Quantity = 2m,
                    Reason = "Two strips found behind the shelf label.",
                    AdjustedBy = Operator,
                });

            Assert.True(adjusted.Succeeded, adjusted.Message);

            var shelf = await BatchAsync(services, batch);
            Assert.Equal(12m, shelf.QuantityOnHand);
            Assert.Equal(12m, shelf.QuantityReceived);

            var movement = Assert.Single(
                await MovementsAsync(services, medicineId),
                candidate => candidate.MovementType == StockMovementType.AdjustmentIncrease);

            Assert.Equal(2m, movement.QuantityDelta);
            Assert.Equal(12m, movement.QuantityAfter);
            Assert.Equal("Two strips found behind the shelf label.", movement.Reason);

            var audit = Assert.Single(await AuditAsync(services, AuditAction.StockAdjusted, batch));
            Assert.Equal("MedicineBatch", audit.EntityType);
            Assert.Equal(batch, audit.EntityId);
            Assert.Equal("Two strips found behind the shelf label.", audit.Reason);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);

            return adjusted.Value!;
        });

        Assert.Equal(StockMovementType.AdjustmentIncrease, outcome.MovementType);
        Assert.Equal(2m, outcome.QuantityDelta);
        Assert.Equal(12m, outcome.QuantityAfter);
        Assert.Equal(Today, outcome.BusinessDate);
    }

    [Fact]
    public async Task An_adjustment_down_removes_stock_nobody_had_recorded_losing()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Adjustment test {Unique()}");
            var (_, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var adjusted = await services.GetRequiredService<IInventoryOperations>().AdjustAsync(
                new StockAdjustmentDraft
                {
                    MedicineBatchId = batchId,
                    IsIncrease = false,
                    Quantity = 3m,
                    Reason = "Three strips dropped and broken on the floor.",
                    AdjustedBy = Operator,
                });

            Assert.True(adjusted.Succeeded, adjusted.Message);

            var shelf = await BatchAsync(services, batch);
            Assert.Equal(7m, shelf.QuantityOnHand);

            var movement = Assert.Single(
                await MovementsAsync(services, medicineId),
                candidate => candidate.MovementType == StockMovementType.AdjustmentDecrease);

            Assert.Equal(-3m, movement.QuantityDelta);
            Assert.Equal(7m, movement.QuantityAfter);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);

            return adjusted.Value!;
        });

        Assert.Equal(StockMovementType.AdjustmentDecrease, outcome.MovementType);
        Assert.Equal(-3m, outcome.QuantityDelta);
        Assert.Equal(7m, outcome.QuantityAfter);
    }

    [Fact]
    public async Task An_adjustment_beyond_what_the_batch_holds_is_refused_and_writes_nothing()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Adjustment test {Unique()}");
            var (_, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 5m, 12.50m, 20.00m));

            var movementsBefore = (await MovementsAsync(services, medicineId)).Count;

            var adjusted = await services.GetRequiredService<IInventoryOperations>().AdjustAsync(
                new StockAdjustmentDraft
                {
                    MedicineBatchId = batchId,
                    IsIncrease = false,
                    Quantity = 6m,
                    Reason = "Clearing more than the shelf holds.",
                    AdjustedBy = Operator,
                });

            Assert.False(adjusted.Succeeded);
            Assert.Contains("has only 5 available", adjusted.Message);
            Assert.Contains("6 requested", adjusted.Message);

            Assert.Equal(movementsBefore, (await MovementsAsync(services, medicineId)).Count);
            Assert.Equal(5m, (await BatchAsync(services, batch)).QuantityOnHand);

            var audit = await AuditAsync(services, AuditAction.StockAdjusted, batch);
            Assert.Empty(audit);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);
        });
    }

    [Fact]
    public async Task An_adjustment_needs_a_batch_a_quantity_and_a_reason()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Adjustment test {Unique()}");
            var (_, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 5m, 12.50m, 20.00m));

            var movementsBefore = (await MovementsAsync(services, medicineId)).Count;
            var corrections = services.GetRequiredService<IInventoryOperations>();

            var noBatch = await corrections.AdjustAsync(new StockAdjustmentDraft
            {
                IsIncrease = true,
                Quantity = 1m,
                Reason = "Nothing chosen yet.",
            });
            Assert.False(noBatch.Succeeded);
            Assert.Equal("Choose the batch whose count is being corrected first.", noBatch.Message);

            var noQuantity = await corrections.AdjustAsync(new StockAdjustmentDraft
            {
                MedicineBatchId = batchId,
                IsIncrease = true,
                Quantity = 0m,
                Reason = "A quantity of nothing.",
            });
            Assert.False(noQuantity.Succeeded);
            Assert.Equal("Enter a quantity greater than zero to adjust the count by.", noQuantity.Message);

            var noReason = await corrections.AdjustAsync(new StockAdjustmentDraft
            {
                MedicineBatchId = batchId,
                IsIncrease = true,
                Quantity = 1m,
                Reason = "   ",
            });
            Assert.False(noReason.Succeeded);
            Assert.Equal(
                "A reason is required, because the adjustment is what the audit trail will show.",
                noReason.Message);

            var unknownBatch = await corrections.AdjustAsync(new StockAdjustmentDraft
            {
                MedicineBatchId = 999_999,
                IsIncrease = true,
                Quantity = 1m,
                Reason = "A batch that does not exist.",
            });
            Assert.False(unknownBatch.Succeeded);
            Assert.Equal(
                "That batch could not be found, so nothing was adjusted. Reload the inventory list and "
                + "choose the batch again.",
                unknownBatch.Message);

            Assert.Equal(movementsBefore, (await MovementsAsync(services, medicineId)).Count);
            Assert.Equal(5m, (await BatchAsync(services, batch)).QuantityOnHand);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);
        });
    }

    [Fact]
    public async Task A_write_off_disposes_of_the_whole_batch_and_blocks_it()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Write-off test {Unique()}");
            var (_, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var writtenOff = await services.GetRequiredService<IInventoryOperations>().WriteOffAsync(
                new StockWriteOffDraft
                {
                    MedicineBatchId = batchId,
                    IsRecall = false,
                    Reason = "Expired stock cleared from the shelf.",
                    WrittenOffBy = Operator,
                });

            Assert.True(writtenOff.Succeeded, writtenOff.Message);

            var shelf = await BatchAsync(services, batch);
            Assert.Equal(0m, shelf.QuantityOnHand);
            Assert.Equal(BatchStatus.WrittenOff, shelf.Status);
            Assert.Equal("Expired stock cleared from the shelf.", shelf.StatusReason);

            var movement = Assert.Single(
                await MovementsAsync(services, medicineId),
                candidate => candidate.MovementType == StockMovementType.ExpiryWriteOff);

            Assert.Equal(-10m, movement.QuantityDelta);
            Assert.Equal(0m, movement.QuantityAfter);
            Assert.Equal("Expired stock cleared from the shelf.", movement.Reason);

            var audit = Assert.Single(await AuditAsync(services, AuditAction.StockWrittenOff, batch));
            Assert.Equal(batch, audit.EntityId);

            // Disposal is the end of the story: nothing is left to deal with, so the batch leaves the
            // quarantine view the moment its stock leaves the ledger.
            var quarantined = await services.GetRequiredService<IStockQueries>()
                .ListQuarantinedAsync(new QuarantinedStockQuery { Term = batch });
            Assert.Empty(quarantined);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);

            return writtenOff.Value!;
        });

        Assert.Equal(StockMovementType.ExpiryWriteOff, outcome.MovementType);
        Assert.Equal(-10m, outcome.QuantityDelta);
        Assert.Equal(0m, outcome.QuantityAfter);
    }

    [Fact]
    public async Task A_recall_disposal_marks_the_batch_recalled()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Write-off test {Unique()}");
            var (_, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 8m, 12.50m, 20.00m));

            var writtenOff = await services.GetRequiredService<IInventoryOperations>().WriteOffAsync(
                new StockWriteOffDraft
                {
                    MedicineBatchId = batchId,
                    IsRecall = true,
                    Reason = "Recall notice RN-88: pulled and destroyed.",
                    WrittenOffBy = Operator,
                });

            Assert.True(writtenOff.Succeeded, writtenOff.Message);

            var shelf = await BatchAsync(services, batch);
            Assert.Equal(0m, shelf.QuantityOnHand);
            Assert.Equal(BatchStatus.Recalled, shelf.Status);

            var movement = Assert.Single(
                await MovementsAsync(services, medicineId),
                candidate => candidate.MovementType == StockMovementType.RecallWriteOff);

            Assert.Equal(-8m, movement.QuantityDelta);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);
        });
    }
    [Fact]
    public async Task A_batch_with_nothing_left_can_be_neither_written_off_nor_quarantined()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Write-off test {Unique()}");
            var (_, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var corrections = services.GetRequiredService<IInventoryOperations>();

            var writtenOff = await corrections.WriteOffAsync(new StockWriteOffDraft
            {
                MedicineBatchId = batchId,
                Reason = "Expired stock cleared from the shelf.",
                WrittenOffBy = Operator,
            });
            Assert.True(writtenOff.Succeeded, writtenOff.Message);

            var movementsBefore = (await MovementsAsync(services, medicineId)).Count;

            var writtenOffAgain = await corrections.WriteOffAsync(new StockWriteOffDraft
            {
                MedicineBatchId = batchId,
                Reason = "Trying to write it off twice.",
                WrittenOffBy = Operator,
            });
            Assert.False(writtenOffAgain.Succeeded);
            Assert.Equal($"Batch '{batch}' has no quantity left to write off.", writtenOffAgain.Message);

            var quarantinedAfterDisposal = await corrections.QuarantineAsync(new StockQuarantineDraft
            {
                MedicineBatchId = batchId,
                IsRecall = true,
                Reason = "Trying to hold back stock that has already gone.",
            });
            Assert.False(quarantinedAfterDisposal.Succeeded);
            Assert.Equal($"Batch '{batch}' has no quantity left to write off.", quarantinedAfterDisposal.Message);

            Assert.Equal(movementsBefore, (await MovementsAsync(services, medicineId)).Count);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);
        });
    }

    [Fact]
    public async Task A_write_off_needs_a_batch_and_a_reason()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Write-off test {Unique()}");
            var (_, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 5m, 12.50m, 20.00m));

            var movementsBefore = (await MovementsAsync(services, medicineId)).Count;
            var corrections = services.GetRequiredService<IInventoryOperations>();

            var noBatch = await corrections.WriteOffAsync(new StockWriteOffDraft
            {
                Reason = "Nothing chosen yet.",
                WrittenOffBy = Operator,
            });
            Assert.False(noBatch.Succeeded);
            Assert.Equal("Choose the batch whose stock is being written off first.", noBatch.Message);

            var noReason = await corrections.WriteOffAsync(new StockWriteOffDraft
            {
                MedicineBatchId = batchId,
                Reason = "   ",
                WrittenOffBy = Operator,
            });
            Assert.False(noReason.Succeeded);
            Assert.Equal(
                "A reason is required - the recall notice, the expiry decision or the damage report this "
                + "write-off is acting on.",
                noReason.Message);

            var unknownBatch = await corrections.WriteOffAsync(new StockWriteOffDraft
            {
                MedicineBatchId = 999_999,
                Reason = "A batch that does not exist.",
                WrittenOffBy = Operator,
            });
            Assert.False(unknownBatch.Succeeded);
            Assert.Equal(
                "That batch could not be found, so nothing was written off. Reload the inventory list and "
                + "choose the batch again.",
                unknownBatch.Message);

            Assert.Equal(movementsBefore, (await MovementsAsync(services, medicineId)).Count);
            Assert.Equal(5m, (await BatchAsync(services, batch)).QuantityOnHand);
        });
    }

    /// <summary>
    /// The hold, not the disposal. A recall notice blocks the batch where it sits: the balance does not
    /// move, the movement ledger gains no row, and the stock simply cannot leave - which is exactly what
    /// the quarantine view then shows the pharmacist as waiting to be dealt with.
    /// </summary>
    [Fact]
    public async Task A_quarantine_holds_stock_where_it_is_without_moving_anything()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Quarantine test {Unique()}");
            var (_, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var movementsBefore = (await MovementsAsync(services, medicineId)).Count;

            var quarantined = await services.GetRequiredService<IInventoryOperations>().QuarantineAsync(
                new StockQuarantineDraft
                {
                    MedicineBatchId = batchId,
                    IsRecall = true,
                    Reason = "Recall notice RN-88: batch pulled pending destruction.",
                    Notes = "Manufacturer letter filed with the pharmacist.",
                });

            Assert.True(quarantined.Succeeded, quarantined.Message);

            var shelf = await BatchAsync(services, batch);
            Assert.Equal(10m, shelf.QuantityOnHand);
            Assert.Equal(BatchStatus.Recalled, shelf.Status);
            Assert.Equal("Recall notice RN-88: batch pulled pending destruction.", shelf.StatusReason);

            Assert.Equal(movementsBefore, (await MovementsAsync(services, medicineId)).Count);

            var audit = Assert.Single(await AuditAsync(services, AuditAction.StockQuarantined, batch));
            Assert.Equal("MedicineBatch", audit.EntityType);
            Assert.Equal(batch, audit.EntityId);
            Assert.Equal("Recall notice RN-88: batch pulled pending destruction.", audit.Reason);
            Assert.Equal("Manufacturer letter filed with the pharmacist.", audit.Detail);

            var held = Assert.Single(
                await services.GetRequiredService<IStockQueries>()
                    .ListQuarantinedAsync(new QuarantinedStockQuery { Term = batch }));

            Assert.Equal(QuarantineSource.Recall, held.Source);
            Assert.Equal(batchId, held.MedicineBatchId);
            Assert.Equal(batch, held.BatchNumber);
            Assert.Equal(10m, held.Quantity);
            Assert.Equal("Recall notice RN-88: batch pulled pending destruction.", held.Reason);
            Assert.Equal(batch, held.Reference);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);

            return quarantined.Value!;
        });

        Assert.Equal(BatchStatus.Recalled, outcome.Status);
        Assert.Equal(10m, outcome.QuantityHeld);
    }

    /// <summary>
    /// The whole point of the hold: a batch under quarantine is on the shelf but cannot leave it. A sale,
    /// a manual adjustment and a supplier return all name the same blocked batch, and all three are
    /// refused without changing the balance.
    /// </summary>
    [Fact]
    public async Task Quarantined_stock_cannot_be_sold_adjusted_or_returned_to_the_supplier()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Quarantine test {Unique()}");
            var (supplierId, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var corrections = services.GetRequiredService<IInventoryOperations>();

            var quarantined = await corrections.QuarantineAsync(new StockQuarantineDraft
            {
                MedicineBatchId = batchId,
                IsRecall = true,
                Reason = "Recall notice RN-88.",
            });
            Assert.True(quarantined.Succeeded, quarantined.Message);

            var movementsBefore = (await MovementsAsync(services, medicineId)).Count;

            var sale = await services.GetRequiredService<ISaleCatalog>().CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                CustomerName = "Blocked sale",
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 1m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 20.00m }],
            });
            Assert.False(sale.Succeeded);
            Assert.Contains("can be dispensed today", sale.Message);

            var adjusted = await corrections.AdjustAsync(new StockAdjustmentDraft
            {
                MedicineBatchId = batchId,
                IsIncrease = false,
                Quantity = 1m,
                Reason = "Trying to move a recalled batch.",
            });
            Assert.False(adjusted.Succeeded);
            Assert.Equal($"Batch '{batch}' is recalled and cannot be moved.", adjusted.Message);

            var returned = await services.GetRequiredService<ISupplierReturnCatalog>().RecordAsync(
                new SupplierReturnDraft
                {
                    SupplierId = supplierId,
                    ReturnedBy = Operator,
                    Reason = "Trying to send a recalled batch back.",
                    Lines = [new SupplierReturnLineDraft { MedicineBatchId = batchId, Quantity = 1m }],
                });
            Assert.False(returned.Succeeded);
            Assert.Contains("cannot be returned to the supplier", returned.Message);

            Assert.Equal(movementsBefore, (await MovementsAsync(services, medicineId)).Count);
            Assert.Equal(10m, (await BatchAsync(services, batch)).QuantityOnHand);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);
        });
    }

    /// <summary>
    /// Disposal is the deliberate second step after the hold: the batch leaves quarantine because its
    /// stock finally leaves the ledger, under the movement the recall demands.
    /// </summary>
    [Fact]
    public async Task A_quarantined_batch_leaves_quarantine_through_disposal()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Quarantine test {Unique()}");
            var (_, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var corrections = services.GetRequiredService<IInventoryOperations>();

            var held = await corrections.QuarantineAsync(new StockQuarantineDraft
            {
                MedicineBatchId = batchId,
                IsRecall = true,
                Reason = "Recall notice RN-88: batch pulled pending destruction.",
            });
            Assert.True(held.Succeeded, held.Message);

            var quarantined = await services.GetRequiredService<IStockQueries>()
                .ListQuarantinedAsync(new QuarantinedStockQuery { Term = batch });
            Assert.Single(quarantined);

            var disposed = await corrections.WriteOffAsync(new StockWriteOffDraft
            {
                MedicineBatchId = batchId,
                IsRecall = true,
                Reason = "Recall notice RN-88: destroyed with the municipal collector.",
                WrittenOffBy = Operator,
            });
            Assert.True(disposed.Succeeded, disposed.Message);

            var shelf = await BatchAsync(services, batch);
            Assert.Equal(0m, shelf.QuantityOnHand);
            Assert.Equal(BatchStatus.Recalled, shelf.Status);

            var movement = Assert.Single(
                await MovementsAsync(services, medicineId),
                candidate => candidate.MovementType == StockMovementType.RecallWriteOff);

            Assert.Equal(-10m, movement.QuantityDelta);

            quarantined = await services.GetRequiredService<IStockQueries>()
                .ListQuarantinedAsync(new QuarantinedStockQuery { Term = batch });
            Assert.Empty(quarantined);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);
        });
    }

    /// <summary>
    /// A batch that is condemned rather than recalled is the same hold under a different name: blocked,
    /// still on the shelf, and listed as a write-off waiting to happen rather than a recall.
    /// </summary>
    [Fact]
    public async Task A_condemned_batch_is_quarantined_as_a_pending_write_off()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Quarantine test {Unique()}");
            var (_, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 6m, 12.50m, 20.00m));

            var quarantined = await services.GetRequiredService<IInventoryOperations>().QuarantineAsync(
                new StockQuarantineDraft
                {
                    MedicineBatchId = batchId,
                    IsRecall = false,
                    Reason = "Condemned by the pharmacist: crushed in transit.",
                });

            Assert.True(quarantined.Succeeded, quarantined.Message);
            Assert.Equal(BatchStatus.WrittenOff, quarantined.Value!.Status);
            Assert.Equal(6m, quarantined.Value.QuantityHeld);

            var held = Assert.Single(
                await services.GetRequiredService<IStockQueries>()
                    .ListQuarantinedAsync(new QuarantinedStockQuery { Term = batch }));

            Assert.Equal(QuarantineSource.WriteOff, held.Source);
            Assert.Equal(6m, held.Quantity);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);
        });
    }

    [Fact]
    public async Task A_quarantine_needs_a_batch_a_reason_and_stock_to_hold()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Quarantine test {Unique()}");
            var (_, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 5m, 12.50m, 20.00m));

            var movementsBefore = (await MovementsAsync(services, medicineId)).Count;
            var corrections = services.GetRequiredService<IInventoryOperations>();

            var noBatch = await corrections.QuarantineAsync(new StockQuarantineDraft
            {
                IsRecall = true,
                Reason = "Nothing chosen yet.",
            });
            Assert.False(noBatch.Succeeded);
            Assert.Equal("Choose the batch whose stock is being held back first.", noBatch.Message);

            var noReason = await corrections.QuarantineAsync(new StockQuarantineDraft
            {
                MedicineBatchId = batchId,
                IsRecall = true,
                Reason = "   ",
            });
            Assert.False(noReason.Succeeded);
            Assert.Equal(
                "A reason is required - the recall notice, the condemnation or the damage report this "
                + "hold is acting on.",
                noReason.Message);

            var unknownBatch = await corrections.QuarantineAsync(new StockQuarantineDraft
            {
                MedicineBatchId = 999_999,
                IsRecall = true,
                Reason = "A batch that does not exist.",
            });
            Assert.False(unknownBatch.Succeeded);
            Assert.Equal(
                "That batch could not be found, so nothing was quarantined. Reload the inventory list and "
                + "choose the batch again.",
                unknownBatch.Message);

            Assert.Equal(movementsBefore, (await MovementsAsync(services, medicineId)).Count);
            Assert.Equal(5m, (await BatchAsync(services, batch)).QuantityOnHand);

            Assert.True((await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync()).IsReconciled);
        });
    }

    /// <summary>
    /// Re-quarantining is allowed rather than refused, because a second notice can arrive while the first
    /// hold is already on: the batch is still one batch, and what it must say is the reason in force now.
    /// </summary>
    [Fact]
    public async Task Re_quarantining_records_the_reason_in_force()
    {
        var batch = $"B-{Unique()}";

        await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Quarantine test {Unique()}");
            var (_, batchId) = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var corrections = services.GetRequiredService<IInventoryOperations>();

            var first = await corrections.QuarantineAsync(new StockQuarantineDraft
            {
                MedicineBatchId = batchId,
                IsRecall = true,
                Reason = "Recall notice RN-88.",
            });
            Assert.True(first.Succeeded, first.Message);

            var second = await corrections.QuarantineAsync(new StockQuarantineDraft
            {
                MedicineBatchId = batchId,
                IsRecall = true,
                Reason = "Recall notice RN-88 extended to the whole batch range.",
            });
            Assert.True(second.Succeeded, second.Message);

            var shelf = await BatchAsync(services, batch);
            Assert.Equal(
                "Recall notice RN-88 extended to the whole batch range.",
                shelf.StatusReason);

            var held = Assert.Single(
                await services.GetRequiredService<IStockQueries>()
                    .ListQuarantinedAsync(new QuarantinedStockQuery { Term = batch }));

            Assert.Equal("Recall notice RN-88 extended to the whole batch range.", held.Reason);

            // Two holds, two audit rows: the history of the notice is the history of the batch.
            var audit = await AuditAsync(services, AuditAction.StockQuarantined, batch);
            Assert.Equal(2, audit.Count);
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
    /// Puts stock on the shelf through the real purchasing workflow, so the batches, their receipt
    /// movements and the purchase behind them are exactly what a pharmacy would have. The supplier the
    /// stock came from is returned with the batch, because corrections can name it later.
    /// </summary>
    private async Task<(int SupplierId, int BatchId)> ReceiveAsync(
        IServiceProvider services,
        int medicineId,
        (string BatchNumber, DateOnly ExpiryDate, decimal Quantity, decimal UnitCost, decimal SellingPrice) line)
    {
        var supplier = await services.GetRequiredService<ISupplierCatalog>().CreateAsync(
            new SupplierDraft { Name = $"Correction test supplier {Unique()}" });
        Assert.True(supplier.Succeeded, supplier.Message);

        var catalog = services.GetRequiredService<IPurchaseCatalog>();

        var created = await catalog.CreateAsync(new PurchaseDraft
        {
            SupplierId = supplier.Value!.Id,
            InvoiceNumber = $"COR-{Unique()}",
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

    /// <summary>A batch with stock still on the shelf, so the assertion also proves it is sellable.</summary>
    private static async Task<BatchStockDto> BatchAsync(IServiceProvider services, string batchNumber)
    {
        var batches = await services.GetRequiredService<IStockQueries>().ListBatchesAsync(
            new BatchStockQuery { Term = batchNumber, InStockOnly = false });

        return Assert.Single(batches);
    }

    private static Task<IReadOnlyList<StockMovementDto>> MovementsAsync(IServiceProvider services, int medicineId)
        => services.GetRequiredService<IStockQueries>()
            .ListMovementsAsync(new StockMovementQuery { MedicineId = medicineId });

    /// <summary>The audit rows about one batch, which every correction must have left behind.</summary>
    private static Task<IReadOnlyList<AuditEntryDto>> AuditAsync(
        IServiceProvider services,
        AuditAction action,
        string batchNumber)
        => services.GetRequiredService<IAuditTrail>().SearchAsync(
            new AuditQuery { Action = action, EntityType = "MedicineBatch", Term = batchNumber, Take = 5 });

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];
}