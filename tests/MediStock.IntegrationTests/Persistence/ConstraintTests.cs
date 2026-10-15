using MediStock.Domain.Catalogue;
using MediStock.Domain.Inventory;
using MediStock.Infrastructure.Persistence;
using Microsoft.Data.SqlClient;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Persistence;

/// <summary>
/// Database constraints are the last line of defence: an application or UI check is never a
/// substitute for them. These tests deliberately bypass the domain and write raw SQL, so they keep
/// working even if a future refactor loosens a validation rule.
/// </summary>
[Collection(TestDatabaseCollection.Name)]
public class ConstraintTests
{
    /// <summary>SQL Server error number for a constraint violation.</summary>
    private const int ConstraintViolation = 547;

    /// <summary>SQL Server error number for a duplicate key.</summary>
    private const int DuplicateKey = 2601;

    private readonly TestDatabaseFixture _fixture;

    public ConstraintTests(TestDatabaseFixture fixture) => _fixture = fixture;

    [Fact]
    public async Task A_second_medicine_cannot_reuse_a_barcode()
    {
        var failure = await _fixture.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<MediStockDbContext>();
            var now = DateTimeOffset.UtcNow;

            db.Medicines.Add(Medicine.Create($"Barcode owner {Unique()}", DosageForm.Tablet, barcode: "8901111111111", nowUtc: now));
            db.Medicines.Add(Medicine.Create($"Barcode thief {Unique()}", DosageForm.Tablet, barcode: "8901111111111", nowUtc: now));

            return await Assert.ThrowsAsync<DbUpdateException>(() => db.SaveChangesAsync());
        });

        var sql = Assert.IsType<SqlException>(failure.InnerException);
        Assert.Equal(DuplicateKey, sql.Number);
        Assert.Contains("UX_Medicines_Barcode", sql.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Many_medicines_may_share_a_missing_barcode()
    {
        var stored = await _fixture.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<MediStockDbContext>();
            var now = DateTimeOffset.UtcNow;

            db.Medicines.Add(Medicine.Create($"No barcode A {Unique()}", DosageForm.Tablet, nowUtc: now));
            db.Medicines.Add(Medicine.Create($"No barcode B {Unique()}", DosageForm.Tablet, nowUtc: now));

            return await db.SaveChangesAsync();
        });

        Assert.Equal(2, stored);
    }

    [Fact]
    public async Task The_database_rejects_a_negative_reorder_threshold()
    {
        var sql = await _fixture.InScopeAsync(async services =>
        {
            var name = $"Negative threshold {Unique()}";
            var db = services.GetRequiredService<MediStockDbContext>();
            db.Medicines.Add(Medicine.Create(name, DosageForm.Tablet, nowUtc: DateTimeOffset.UtcNow));
            await db.SaveChangesAsync();

            return await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE Medicines SET ReorderThreshold = -1 WHERE DisplayName = {0}", name));
        });

        Assert.Equal(ConstraintViolation, sql.Number);
        Assert.Contains("CK_Medicines_ReorderThreshold_NonNegative", sql.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_batch_cannot_hold_more_stock_than_was_received()
    {
        var error = await _fixture.InScopeAsync(async services =>
        {
            var (_, batchId) = await CreateBatchAsync(services, quantity: 10m);
            var db = services.GetRequiredService<MediStockDbContext>();

            return await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE MedicineBatches SET QuantityOnHand = QuantityReceived + 1 WHERE Id = {0}", batchId));
        });

        Assert.Equal(ConstraintViolation, error.Number);
        Assert.Contains("CK_MedicineBatches_QuantityOnHand_NonNegative", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_batch_cannot_hold_a_negative_balance()
    {
        var error = await _fixture.InScopeAsync(async services =>
        {
            var (_, batchId) = await CreateBatchAsync(services, quantity: 10m);
            var db = services.GetRequiredService<MediStockDbContext>();

            return await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE MedicineBatches SET QuantityOnHand = -1 WHERE Id = {0}", batchId));
        });

        Assert.Equal(ConstraintViolation, error.Number);
    }

    [Fact]
    public async Task A_batch_cannot_be_received_with_zero_quantity()
    {
        var error = await _fixture.InScopeAsync(async services =>
        {
            var (_, batchId) = await CreateBatchAsync(services, quantity: 10m);
            var db = services.GetRequiredService<MediStockDbContext>();

            return await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE MedicineBatches SET QuantityOnHand = 0, QuantityReceived = 0 WHERE Id = {0}", batchId));
        });

        Assert.Equal(ConstraintViolation, error.Number);
        Assert.Contains("CK_MedicineBatches_QuantityReceived_Positive", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_batch_cannot_expire_on_or_before_the_day_it_was_received()
    {
        var error = await _fixture.InScopeAsync(async services =>
        {
            var (_, batchId) = await CreateBatchAsync(services);
            var db = services.GetRequiredService<MediStockDbContext>();

            return await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE MedicineBatches SET ExpiryDate = ReceivedOn WHERE Id = {0}", batchId));
        });

        Assert.Equal(ConstraintViolation, error.Number);
        Assert.Contains("CK_MedicineBatches_ExpiryAfterReceipt", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_batch_cannot_hold_a_negative_price()
    {
        var error = await _fixture.InScopeAsync(async services =>
        {
            var (_, batchId) = await CreateBatchAsync(services);
            var db = services.GetRequiredService<MediStockDbContext>();

            return await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "UPDATE MedicineBatches SET SellingPrice = -0.01 WHERE Id = {0}", batchId));
        });

        Assert.Equal(ConstraintViolation, error.Number);
        Assert.Contains("CK_MedicineBatches_Prices_NonNegative", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_stock_movement_cannot_reference_a_missing_batch()
    {
        var error = await _fixture.InScopeAsync(async services =>
        {
            var db = services.GetRequiredService<MediStockDbContext>();

            return await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO StockMovements (MedicineBatchId, MedicineId, MovementType, QuantityDelta, QuantityAfter, BusinessDate, PerformedBy, OccurredAtUtc) "
                + "VALUES (999999, 999999, 1, 1, 1, '2026-04-01', 'Test', SYSDATETIMEOFFSET())"));
        });

        Assert.Equal(ConstraintViolation, error.Number);
    }

    [Fact]
    public async Task A_stock_movement_must_change_the_quantity()
    {
        var error = await _fixture.InScopeAsync(async services =>
        {
            var (medicineId, batchId) = await CreateBatchAsync(services);
            var db = services.GetRequiredService<MediStockDbContext>();

            return await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO StockMovements (MedicineBatchId, MedicineId, MovementType, QuantityDelta, QuantityAfter, BusinessDate, PerformedBy, OccurredAtUtc) "
                + "VALUES ({0}, {1}, 1, 0, 1, '2026-04-01', 'Test', SYSDATETIMEOFFSET())", batchId, medicineId));
        });

        Assert.Equal(ConstraintViolation, error.Number);
        Assert.Contains("CK_StockMovements_QuantityDelta_NonZero", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_corrective_stock_movement_must_carry_a_reason()
    {
        var error = await _fixture.InScopeAsync(async services =>
        {
            var (medicineId, batchId) = await CreateBatchAsync(services);
            var db = services.GetRequiredService<MediStockDbContext>();

            // Movement type 5 is AdjustmentDecrease, which the schema refuses to record without a reason.
            return await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "INSERT INTO StockMovements (MedicineBatchId, MedicineId, MovementType, QuantityDelta, QuantityAfter, BusinessDate, PerformedBy, OccurredAtUtc) "
                + "VALUES ({0}, {1}, 5, -1, 1, '2026-04-01', 'Test', SYSDATETIMEOFFSET())", batchId, medicineId));
        });

        Assert.Equal(ConstraintViolation, error.Number);
        Assert.Contains("CK_StockMovements_Reason_Required", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Currency_columns_store_two_decimal_places()
    {
        var stored = await _fixture.InScopeAsync(async services =>
        {
            var (_, batchId) = await CreateBatchAsync(services);
            var db = services.GetRequiredService<MediStockDbContext>();

            await db.Database.ExecuteSqlRawAsync(
                "UPDATE MedicineBatches SET PurchaseCost = 1.105 WHERE Id = {0}", batchId);

            return await db.MedicineBatches.AsNoTracking()
                .Where(b => b.Id == batchId)
                .Select(b => b.PurchaseCost)
                .SingleAsync();
        });

        // The column is decimal(18,2), so a third decimal place can never reach the ledger.
        Assert.Equal(1.11m, stored);
    }

    [Fact]
    public async Task A_batch_that_has_movements_cannot_be_deleted()
    {
        var error = await _fixture.InScopeAsync(async services =>
        {
            // CreateBatchAsync already recorded the receipt movement, so this batch is pinned by the
            // foreign key and any attempt to delete it must fail.
            var (_, batchId) = await CreateBatchAsync(services);
            var db = services.GetRequiredService<MediStockDbContext>();

            return await Assert.ThrowsAsync<SqlException>(() => db.Database.ExecuteSqlRawAsync(
                "DELETE FROM MedicineBatches WHERE Id = {0}", batchId));
        });

        Assert.Equal(ConstraintViolation, error.Number);
    }

    /// <summary>
    /// Creates a medicine and a received batch, together with the receipt movement that balances it.
    /// The movement is not incidental: every batch in the database must be reconcilable against the
    /// ledger, so a helper that created stock without a movement would put the test database into a
    /// state the application itself never produces.
    /// </summary>
    private async Task<(int MedicineId, int BatchId)> CreateBatchAsync(IServiceProvider services, decimal quantity = 10m)
    {
        var db = services.GetRequiredService<MediStockDbContext>();
        var now = DateTimeOffset.UtcNow;
        var today = BusinessTime.Today;

        var medicine = Medicine.Create($"Constraint test {Unique()}", DosageForm.Tablet, nowUtc: now);
        db.Medicines.Add(medicine);
        await db.SaveChangesAsync();

        var batch = MedicineBatch.Receive(
            medicine.Id, "B-1", today.AddYears(1), quantity, 1m, 2m, today, nowUtc: now);
        db.MedicineBatches.Add(batch);
        await db.SaveChangesAsync();

        db.StockMovements.Add(StockMovement.Record(
            medicine.Id, batch.Id, StockMovementType.Receipt, quantity, batch.QuantityOnHand, today, "Test", nowUtc: now));
        await db.SaveChangesAsync();

        return (medicine.Id, batch.Id);
    }

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];
}
