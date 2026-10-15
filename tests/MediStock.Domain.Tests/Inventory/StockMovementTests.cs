using MediStock.Domain.Common;
using MediStock.Domain.Inventory;

namespace MediStock.Domain.Tests.Inventory;

public class StockMovementTests
{
    private static readonly DateOnly Today = new(2026, 4, 1);

    private static StockMovement Record(
        StockMovementType type = StockMovementType.Receipt,
        decimal delta = 10m,
        decimal after = 10m,
        string? reason = null)
        => StockMovement.Record(1, 1, type, delta, after, Today, "A. Pharmacist", reason: reason);

    [Fact]
    public void Record_captures_the_ledger_entry()
    {
        var movement = StockMovement.Record(
            1, 1, StockMovementType.Receipt, 10m, 10m, Today, "A. Pharmacist", reference: "INV-1");

        Assert.Equal("INV-1", movement.Reference);
        Assert.Equal(1, movement.MedicineId);
        Assert.Equal(1, movement.MedicineBatchId);
        Assert.Equal(10m, movement.QuantityDelta);
        Assert.Equal(10m, movement.QuantityAfter);
        Assert.Equal(Today, movement.BusinessDate);
        Assert.Equal("A. Pharmacist", movement.PerformedBy);
    }

    [Fact]
    public void Record_rejects_an_undefined_movement_type()
        => Assert.Throws<DomainValidationException>(() => Record(type: (StockMovementType)99));

    [Fact]
    public void Record_rejects_a_movement_that_changes_nothing()
        => Assert.Throws<DomainValidationException>(() => Record(delta: 0m));

    [Fact]
    public void Record_rejects_a_negative_balance_after_the_movement()
        => Assert.Throws<DomainValidationException>(() => Record(delta: -5m, after: -1m));

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 0)]
    [InlineData(-1, 1)]
    public void Record_rejects_a_movement_without_a_real_reference(int medicineId, int batchId)
        => Assert.Throws<DomainValidationException>(
            () => StockMovement.Record(medicineId, batchId, StockMovementType.Receipt, 1m, 1m, Today, "A. Pharmacist"));

    [Theory]
    [InlineData(StockMovementType.AdjustmentIncrease)]
    [InlineData(StockMovementType.AdjustmentDecrease)]
    [InlineData(StockMovementType.ExpiryWriteOff)]
    [InlineData(StockMovementType.RecallWriteOff)]
    [InlineData(StockMovementType.ReceiptReversal)]
    public void Record_requires_a_reason_for_corrective_movements(StockMovementType type)
    {
        Assert.Throws<DomainValidationException>(() => Record(type: type));
        Assert.NotNull(Record(type: type, reason: "Stock count correction.").Reason);
    }

    [Theory]
    [InlineData(StockMovementType.Receipt)]
    [InlineData(StockMovementType.Sale)]
    [InlineData(StockMovementType.SalesReturn)]
    [InlineData(StockMovementType.SupplierReturn)]
    public void Record_does_not_demand_a_reason_for_routine_movements(StockMovementType type)
    {
        Assert.False(StockMovement.RequiresReason(type));
        Assert.Null(Record(type: type).Reason);
    }

    [Fact]
    public void Record_requires_an_operator()
        => Assert.Throws<DomainValidationException>(
            () => StockMovement.Record(1, 1, StockMovementType.Receipt, 1m, 1m, Today, "   "));

    [Fact]
    public void Record_trims_and_length_checks_free_text()
    {
        var movement = StockMovement.Record(
            1, 1, StockMovementType.AdjustmentDecrease, -1m, 9m, Today, "A. Pharmacist", reason: "  Breakage  ", reference: "  NOTE-1  ");

        Assert.Equal("Breakage", movement.Reason);
        Assert.Equal("NOTE-1", movement.Reference);
    }

    [Fact]
    public void Record_rejects_an_over_long_reason()
        => Assert.Throws<DomainValidationException>(
            () => Record(type: StockMovementType.AdjustmentDecrease, reason: new string('x', StockMovement.ReasonMaxLength + 1)));
}
