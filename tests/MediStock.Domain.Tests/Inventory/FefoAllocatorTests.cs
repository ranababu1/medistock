using MediStock.Domain.Inventory;

namespace MediStock.Domain.Tests.Inventory;

public class FefoAllocatorTests
{
    private static readonly DateOnly Today = new(2026, 4, 1);

    private static BatchAvailability Batch(
        int id,
        string expiry,
        decimal quantity,
        BatchStatus status = BatchStatus.Active)
        => new(id, $"B{id:000}", DateOnly.Parse(expiry, System.Globalization.CultureInfo.InvariantCulture), quantity, status);

    [Fact]
    public void Allocate_takes_the_earliest_expiry_first()
    {
        var batches = new[]
        {
            Batch(1, "2027-01-31", 10m),
            Batch(2, "2026-06-30", 10m),
            Batch(3, "2026-12-31", 10m),
        };

        var allocations = FefoAllocator.Allocate(batches, 5m, Today);

        Assert.Single(allocations);
        Assert.Equal(2, allocations[0].BatchId);
        Assert.Equal(5m, allocations[0].Quantity);
    }

    [Fact]
    public void Allocate_spills_over_into_the_next_earliest_batch()
    {
        var batches = new[]
        {
            Batch(1, "2027-01-31", 10m),
            Batch(2, "2026-06-30", 4m),
        };

        var allocations = FefoAllocator.Allocate(batches, 10m, Today);

        Assert.Equal(2, allocations.Count);
        Assert.Equal(4m, allocations[0].Quantity);
        Assert.Equal(6m, allocations[1].Quantity);
        Assert.Equal(10m, allocations.Sum(a => a.Quantity));
    }

    [Fact]
    public void Allocate_breaks_expiry_ties_by_batch_id_so_runs_are_repeatable()
    {
        var batches = new[]
        {
            Batch(7, "2026-06-30", 3m),
            Batch(3, "2026-06-30", 3m),
        };

        var first = FefoAllocator.Allocate(batches, 6m, Today);
        var second = FefoAllocator.Allocate(batches.Reverse(), 6m, Today);

        Assert.Equal(new[] { 3, 7 }, first.Select(a => a.BatchId));
        Assert.Equal(first.Select(a => a.BatchId), second.Select(a => a.BatchId));
    }

    [Fact]
    public void Allocate_ignores_expired_stock()
    {
        var batches = new[]
        {
            Batch(1, "2026-03-31", 50m), // expired yesterday
            Batch(2, "2026-06-30", 2m),
        };

        var allocations = FefoAllocator.Allocate(batches, 2m, Today);

        Assert.Single(allocations);
        Assert.Equal(2, allocations[0].BatchId);
    }

    [Fact]
    public void Allocate_ignores_batches_that_are_not_active_or_are_empty()
    {
        var batches = new[]
        {
            Batch(1, "2026-06-30", 50m, BatchStatus.Recalled),
            Batch(2, "2026-06-30", 50m, BatchStatus.WrittenOff),
            Batch(3, "2026-06-30", 0m),
            Batch(4, "2026-07-31", 8m),
        };

        var allocations = FefoAllocator.Allocate(batches, 8m, Today);

        Assert.Single(allocations);
        Assert.Equal(4, allocations[0].BatchId);
    }

    [Fact]
    public void Allocate_refuses_a_short_pick_instead_of_partially_fulfilling_it()
    {
        var batches = new[]
        {
            Batch(1, "2026-06-30", 3m),
            Batch(2, "2026-03-31", 100m), // expired, so not eligible
        };

        var exception = Assert.Throws<InsufficientStockException>(
            () => FefoAllocator.Allocate(batches, 4m, Today));

        Assert.Contains("Insufficient stock", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Allocate_accepts_an_exact_fit()
    {
        var allocations = FefoAllocator.Allocate([Batch(1, "2026-06-30", 6m)], 6m, Today);

        Assert.Single(allocations);
        Assert.Equal(6m, allocations[0].Quantity);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Allocate_rejects_a_non_positive_requirement(int required)
        => Assert.Throws<ArgumentOutOfRangeException>(
            () => FefoAllocator.Allocate([Batch(1, "2026-06-30", 6m)], required, Today));

    [Fact]
    public void Allocate_rejects_a_missing_batch_list()
        => Assert.Throws<ArgumentNullException>(() => FefoAllocator.Allocate(null!, 1m, Today));

    [Fact]
    public void ToAvailability_projects_entity_state_the_allocator_understands()
    {
        var received = MedicineBatch.Receive(
            medicineId: 1,
            batchNumber: "PCM2411",
            expiryDate: Today.AddMonths(6),
            quantity: 100m,
            purchaseCost: 1.10m,
            sellingPrice: 2.00m,
            receivedOn: Today.AddDays(-10));

        var availability = FefoAllocator.ToAvailability([received]);

        Assert.Single(availability);
        Assert.Equal("PCM2411", availability[0].BatchNumber);
        Assert.Equal(100m, availability[0].AvailableQuantity);
        Assert.Equal(received.ExpiryDate, availability[0].ExpiryDate);
    }
}
