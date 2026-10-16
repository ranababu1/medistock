using MediStock.Domain.Common;
using MediStock.Domain.Inventory;

namespace MediStock.Domain.Tests.Inventory;

public class MedicineBatchTests
{
    private static readonly DateOnly Today = new(2026, 4, 1);
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

    private static MedicineBatch Received(
        decimal quantity = 100m,
        DateOnly? expiry = null,
        DateOnly? receivedOn = null,
        string batchNumber = "PCM2411")
        => MedicineBatch.Receive(
            medicineId: 1,
            batchNumber: batchNumber,
            expiryDate: expiry ?? Today.AddMonths(6),
            quantity: quantity,
            purchaseCost: 1.10m,
            sellingPrice: 2.00m,
            receivedOn: receivedOn ?? Today.AddDays(-7),
            supplierId: 1,
            supplierInvoiceNumber: "INV-1",
            nowUtc: Now);

    [Fact]
    public void Receive_starts_from_the_received_quantity()
    {
        var batch = Received(120m);

        Assert.Equal(120m, batch.QuantityReceived);
        Assert.Equal(120m, batch.QuantityOnHand);
        Assert.Equal(BatchStatus.Active, batch.Status);
        Assert.True(batch.IsSellableOn(Today));
    }

    [Fact]
    public void Receive_requires_an_existing_medicine()
        => Assert.Throws<DomainValidationException>(
            () => MedicineBatch.Receive(0, "PCM2411", Today.AddMonths(6), 1m, 1m, 1m, Today));

    [Theory]
    [InlineData(0)]
    [InlineData(-5)]
    public void Receive_requires_a_positive_quantity(int quantity)
        => Assert.Throws<DomainValidationException>(
            () => MedicineBatch.Receive(1, "PCM2411", Today.AddMonths(6), quantity, 1m, 1m, Today));

    [Fact]
    public void Receive_refuses_stock_that_is_already_expired()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => Received(expiry: Today.AddDays(-1), receivedOn: Today));

        Assert.Contains("Already-expired stock cannot be received.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Receive_refuses_a_batch_that_expires_on_the_day_it_arrives()
        => Assert.Throws<DomainValidationException>(() => Received(expiry: Today, receivedOn: Today));

    [Fact]
    public void Receive_requires_a_batch_number()
        => Assert.Throws<DomainValidationException>(() => Received(batchNumber: "  "));

    [Fact]
    public void Receive_rounds_money_to_two_places()
    {
        var batch = MedicineBatch.Receive(1, "PCM2411", Today.AddMonths(6), 10m, 1.105m, 2.005m, Today.AddDays(-1));

        Assert.Equal(1.11m, batch.PurchaseCost);
        Assert.Equal(2.01m, batch.SellingPrice);
    }

    [Fact]
    public void DecreaseQuantity_prevents_a_negative_balance()
    {
        var batch = Received(10m);

        Assert.Throws<InsufficientStockException>(() => batch.DecreaseQuantity(11m, Now));
        Assert.Equal(10m, batch.QuantityOnHand);
    }

    [Fact]
    public void DecreaseQuantity_to_zero_marks_the_batch_depleted()
    {
        var batch = Received(10m);

        batch.DecreaseQuantity(10m, Now);

        Assert.Equal(0m, batch.QuantityOnHand);
        Assert.Equal(BatchStatus.Depleted, batch.Status);
        Assert.False(batch.IsSellableOn(Today));
    }

    [Fact]
    public void IncreaseQuantity_reactivates_a_depleted_batch()
    {
        var batch = Received(10m);
        batch.DecreaseQuantity(10m, Now);

        batch.IncreaseQuantity(4m, Now);

        Assert.Equal(4m, batch.QuantityOnHand);
        Assert.Equal(14m, batch.QuantityReceived);
        Assert.Equal(BatchStatus.Active, batch.Status);
    }

    [Fact]
    public void Recalled_stock_cannot_be_moved()
    {
        var batch = Received(10m);
        batch.WriteOff("Manufacturer recall notice RC-14.", BatchStatus.Recalled, Now);

        Assert.Throws<DomainValidationException>(() => batch.DecreaseQuantity(1m, Now));
        Assert.Throws<DomainValidationException>(() => batch.IncreaseQuantity(1m, Now));
    }

    [Fact]
    public void WriteOff_requires_a_reason()
    {
        var batch = Received(10m);

        Assert.Throws<DomainValidationException>(() => batch.WriteOff("   ", BatchStatus.WrittenOff, Now));
    }

    [Fact]
    public void WriteOff_requires_stock_to_remove()
    {
        var batch = Received(10m);
        batch.DecreaseQuantity(10m, Now);

        Assert.Throws<DomainValidationException>(() => batch.WriteOff("Damaged in transit.", BatchStatus.WrittenOff, Now));
    }

    [Theory]
    [InlineData(BatchStatus.Active)]
    [InlineData(BatchStatus.Depleted)]
    public void WriteOff_rejects_a_status_that_is_not_a_write_off(BatchStatus status)
    {
        var batch = Received(10m);

        Assert.Throws<DomainValidationException>(() => batch.WriteOff("Reason.", status, Now));
    }

    [Fact]
    public void WriteOff_keeps_the_balance_for_the_audit_trail()
    {
        var batch = Received(10m);

        batch.WriteOff("Damaged in transit.", BatchStatus.WrittenOff, Now);

        Assert.Equal(BatchStatus.WrittenOff, batch.Status);
        Assert.Equal("Damaged in transit.", batch.StatusReason);
        // The quantity is deliberately untouched: a matching movement records the loss.
        Assert.Equal(10m, batch.QuantityOnHand);
        Assert.False(batch.IsSellableOn(Today));
    }

    [Fact]
    public void WriteOffRemainingStock_disposes_the_whole_balance_in_one_step()
    {
        var batch = Received(10m);

        var disposed = batch.WriteOffRemainingStock("Expired; destroyed under the disposal SOP.", BatchStatus.WrittenOff, Now);

        Assert.Equal(10m, disposed);
        Assert.Equal(0m, batch.QuantityOnHand);
        Assert.Equal(BatchStatus.WrittenOff, batch.Status);
        Assert.Equal("Expired; destroyed under the disposal SOP.", batch.StatusReason);
        Assert.False(batch.IsSellableOn(Today));
    }

    [Fact]
    public void WriteOffRemainingStock_can_dispose_a_batch_that_was_marked_first()
    {
        // Mark-then-dispose is the recall workflow: the batch is blocked while the notice is acted on,
        // and only when the goods leave does the balance move. No sequence of the older operations
        // could do this, because a blocked batch refused to be decreased.
        var batch = Received(10m);
        batch.WriteOff("Manufacturer recall RC-14.", BatchStatus.Recalled, Now);

        var disposed = batch.WriteOffRemainingStock("Returned under recall RC-14.", BatchStatus.Recalled, Now);

        Assert.Equal(10m, disposed);
        Assert.Equal(0m, batch.QuantityOnHand);
        Assert.Equal(BatchStatus.Recalled, batch.Status);
        Assert.Equal("Returned under recall RC-14.", batch.StatusReason);
    }

    [Fact]
    public void WriteOffRemainingStock_from_an_expired_batch_records_the_outcome_not_depletion()
    {
        var batch = Received(10m, expiry: Today.AddDays(-1), receivedOn: Today.AddDays(-30));
        batch.RefreshExpiryStatus(Today, Now);

        batch.WriteOffRemainingStock("Expired on the shelf.", BatchStatus.WrittenOff, Now);

        // Destroyed, not merely sold out: a Depleted status would hide that the stock was written off.
        Assert.Equal(BatchStatus.WrittenOff, batch.Status);
        Assert.Equal(0m, batch.QuantityOnHand);
    }

    [Fact]
    public void WriteOffRemainingStock_refuses_an_empty_batch()
    {
        var batch = Received(10m);
        batch.DecreaseQuantity(10m, Now);

        var exception = Assert.Throws<DomainValidationException>(
            () => batch.WriteOffRemainingStock("Expired.", BatchStatus.WrittenOff, Now));

        Assert.Contains("has no quantity left to write off.", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void WriteOffRemainingStock_requires_a_reason()
        => Assert.Throws<DomainValidationException>(
            () => Received(10m).WriteOffRemainingStock("   ", BatchStatus.WrittenOff, Now));

    [Theory]
    [InlineData(BatchStatus.Active)]
    [InlineData(BatchStatus.Depleted)]
    [InlineData(BatchStatus.Expired)]
    public void WriteOffRemainingStock_rejects_a_status_that_is_not_an_outcome(BatchStatus status)
    {
        var batch = Received(10m);

        Assert.Throws<DomainValidationException>(() => batch.WriteOffRemainingStock("Reason.", status, Now));
    }

    [Fact]
    public void RefreshExpiryStatus_only_flags_batches_that_are_genuinely_expired()
    {
        var batch = Received(10m, expiry: Today.AddDays(1), receivedOn: Today.AddDays(-30));

        Assert.False(batch.RefreshExpiryStatus(Today, Now));
        Assert.True(batch.RefreshExpiryStatus(Today.AddDays(2), Now));
        Assert.Equal(BatchStatus.Expired, batch.Status);
        Assert.NotNull(batch.StatusReason);
    }

    [Fact]
    public void RefreshExpiryStatus_ignores_an_empty_batch()
    {
        var batch = Received(10m, expiry: Today.AddDays(1), receivedOn: Today.AddDays(-30));
        batch.DecreaseQuantity(10m, Now);

        Assert.False(batch.RefreshExpiryStatus(Today.AddDays(2), Now));
        Assert.Equal(BatchStatus.Depleted, batch.Status);
    }
}
