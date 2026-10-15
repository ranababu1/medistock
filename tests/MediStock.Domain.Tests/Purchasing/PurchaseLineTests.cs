using MediStock.Domain.Common;
using MediStock.Domain.Purchasing;

namespace MediStock.Domain.Tests.Purchasing;

/// <summary>
/// Line-level rules. A purchase line is the only place where a batch number, an expiry date and a
/// cost are captured before the stock exists, so its validation is the first gate against bad
/// inventory ever entering the system.
/// </summary>
public class PurchaseLineTests
{
    private const int MedicineId = 11;

    private static readonly DateTimeOffset Now = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly Expiry = new(2028, 6, 30);

    private static PurchaseLine AddLine(
        decimal quantity = 10m,
        decimal unitCost = 12.50m,
        decimal sellingPrice = 18m,
        string batchNumber = "B-7781",
        int medicineId = MedicineId)
    {
        var purchase = Purchase.Create(7, "SUN-1001", new DateOnly(2026, 4, 1), nowUtc: Now);
        return purchase.AddLine(medicineId, batchNumber, Expiry, quantity, unitCost, sellingPrice, Now);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-0.001)]
    public void Quantity_must_be_greater_than_zero(decimal quantity)
    {
        var error = Assert.Throws<DomainValidationException>(() => AddLine(quantity: quantity));

        Assert.Contains("greater than zero", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Unit_cost_cannot_be_negative()
        => Assert.Throws<DomainValidationException>(() => AddLine(unitCost: -0.01m));

    [Fact]
    public void Selling_price_cannot_be_negative()
        => Assert.Throws<DomainValidationException>(() => AddLine(sellingPrice: -0.01m));

    [Fact]
    public void A_free_item_is_allowed_as_a_zero_cost()
    {
        var line = AddLine(unitCost: 0m, sellingPrice: 0m);

        Assert.Equal(0m, line.LineTotal);
    }

    [Fact]
    public void A_line_must_reference_an_existing_medicine()
        => Assert.Throws<DomainValidationException>(() => AddLine(medicineId: 0));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Batch_number_is_required(string? batchNumber)
        => Assert.Throws<DomainValidationException>(() => AddLine(batchNumber: batchNumber!));

    [Fact]
    public void Batch_number_must_fit_the_column()
        => Assert.Throws<DomainValidationException>(
            () => AddLine(batchNumber: new string('B', PurchaseLine.BatchNumberMaxLength + 1)));

    [Fact]
    public void Batch_number_is_trimmed()
        => Assert.Equal("B-7781", AddLine(batchNumber: "   B-7781  ").BatchNumber);

    [Fact]
    public void Money_is_rounded_half_away_from_zero_never_to_even()
    {
        var line = AddLine(quantity: 1m, unitCost: 2.345m, sellingPrice: 4.005m);

        Assert.Equal(2.35m, line.UnitCost);
        Assert.Equal(4.01m, line.SellingPrice);
    }

    [Fact]
    public void Line_total_is_the_rounded_unit_cost_times_the_quantity()
    {
        var line = AddLine(quantity: 3m, unitCost: 2.345m);

        Assert.Equal(2.35m, line.UnitCost);
        Assert.Equal(7.05m, line.LineTotal);
    }

    [Fact]
    public void Line_total_rounds_the_result_not_just_the_inputs()
    {
        var line = AddLine(quantity: 2.5m, unitCost: 1.333m);

        Assert.Equal(1.33m, line.UnitCost);
        Assert.Equal(3.33m, line.LineTotal);
    }

    [Fact]
    public void A_line_is_not_received_until_the_purchase_is()
    {
        var line = AddLine();

        Assert.Equal(0m, line.QuantityReceived);
        Assert.Null(line.MedicineBatchId);
        Assert.Null(line.MedicineBatch);
    }
}
