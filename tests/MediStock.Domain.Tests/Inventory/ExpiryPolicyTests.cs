using MediStock.Domain.Inventory;

namespace MediStock.Domain.Tests.Inventory;

/// <summary>
/// Locks in the interpretation recorded in docs/decisions/0002-expiry-date-interpretation.md:
/// the expiry date is the last day the batch may be dispensed.
/// </summary>
public class ExpiryPolicyTests
{
    private static readonly DateOnly Expiry = new(2027, 3, 31);

    [Fact]
    public void A_batch_is_sellable_on_its_expiry_date()
        => Assert.False(ExpiryPolicy.IsExpired(Expiry, Expiry));

    [Fact]
    public void A_batch_is_expired_the_day_after_its_expiry_date()
        => Assert.True(ExpiryPolicy.IsExpired(Expiry, Expiry.AddDays(1)));

    [Fact]
    public void A_batch_is_sellable_before_its_expiry_date()
        => Assert.False(ExpiryPolicy.IsExpired(Expiry, Expiry.AddDays(-1)));

    [Theory]
    [InlineData(BatchStatus.Depleted, 0, false)]
    [InlineData(BatchStatus.Active, 0, false)]
    [InlineData(BatchStatus.Active, 5, true)]
    [InlineData(BatchStatus.Recalled, 5, false)]
    [InlineData(BatchStatus.WrittenOff, 5, false)]
    [InlineData(BatchStatus.Expired, 5, false)]
    public void Only_active_batches_with_stock_are_sellable(BatchStatus status, int quantity, bool expected)
        => Assert.Equal(expected, ExpiryPolicy.IsSellable(status, Expiry, quantity, Expiry.AddDays(-30)));

    [Fact]
    public void An_expired_batch_is_never_sellable_even_with_stock()
        => Assert.False(ExpiryPolicy.IsSellable(BatchStatus.Active, Expiry, 10m, Expiry.AddDays(1)));

    [Fact]
    public void DaysUntilExpiry_is_negative_once_expired()
    {
        Assert.Equal(0, ExpiryPolicy.DaysUntilExpiry(Expiry, Expiry));
        Assert.Equal(30, ExpiryPolicy.DaysUntilExpiry(Expiry, Expiry.AddDays(-30)));
        Assert.Equal(-1, ExpiryPolicy.DaysUntilExpiry(Expiry, Expiry.AddDays(1)));
    }

    [Fact]
    public void IsSellable_rejects_a_null_batch()
        => Assert.Throws<ArgumentNullException>(() => ExpiryPolicy.IsSellable(null!, Expiry));
}
