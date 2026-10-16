using MediStock.Domain.Common;
using MediStock.Domain.Inventory;
using MediStock.Domain.Sales;

namespace MediStock.Domain.Tests.Sales;

/// <summary>
/// The money on an invoice and the rules that make it an immutable document: how a line is priced, how
/// the invoice totals are built from the rounded lines, and when a cancellation is allowed.
/// </summary>
public class SaleTests
{
    private const int MedicineId = 11;

    private static readonly DateOnly BusinessDate = new(2026, 5, 4);
    private static readonly DateOnly Expiry = new(2028, 6, 30);
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 9, 0, 0, TimeSpan.Zero);

    private static SaleLine Line(
        int lineNumber = 1,
        decimal quantity = 4m,
        decimal unitPrice = 7.50m,
        decimal unitCost = 5m,
        decimal discountAmount = 0m,
        decimal taxRatePercent = 5m,
        decimal? allocatedQuantity = null,
        int batchId = 101)
        => SaleLine.Create(
            lineNumber,
            MedicineId,
            "Paracetamol 500 mg",
            quantity,
            unitPrice,
            unitCost,
            discountAmount,
            taxRatePercent,
            [new BatchAllocation(batchId, $"B-{batchId}", Expiry, allocatedQuantity ?? quantity)]);

    private static Sale Invoice(params SaleLine[] lines)
    {
        // Identity as the database would have assigned it: a return line credits an invoice line by key.
        var identified = lines.Select((line, index) => line.WithId(500 + index)).ToArray();

        return Sale
            .Complete(
                "INV-2026-000042",
                BusinessDate,
                "asha",
                identified,
                [Payment.Record(PaymentMethod.Cash, SaleTotals.From(identified).Total, nowUtc: Now)],
                nowUtc: Now)
            .WithId(900);
    }

    [Fact]
    public void A_line_is_priced_in_the_documented_order()
    {
        var line = Line(quantity: 4m, unitPrice: 7.50m, unitCost: 5m, discountAmount: 2m, taxRatePercent: 5m);

        Assert.Equal(30.00m, line.GrossAmount);
        Assert.Equal(2.00m, line.DiscountAmount);
        Assert.Equal(28.00m, line.TaxableAmount);
        Assert.Equal(1.40m, line.TaxAmount);
        Assert.Equal(29.40m, line.LineTotal);
        Assert.Equal(20.00m, line.LineCost);
        Assert.Equal(8.00m, line.Margin);
    }

    [Fact]
    public void Tax_is_rounded_half_away_from_zero()
    {
        // 3.15 of taxable at 12.5 percent is 0.39375, which is a paisa rounded up, not banked.
        var line = Line(quantity: 3m, unitPrice: 1.05m, unitCost: 0.60m, taxRatePercent: 12.5m);

        Assert.Equal(3.15m, line.GrossAmount);
        Assert.Equal(0.39m, line.TaxAmount);
        Assert.Equal(3.54m, line.LineTotal);
    }

    [Fact]
    public void A_line_must_be_allocated_to_the_quantity_it_sells()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => Line(quantity: 4m, allocatedQuantity: 3m));

        Assert.Contains("account for", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_line_must_come_out_of_at_least_one_batch()
        => Assert.Throws<DomainValidationException>(() => SaleLine.Create(
            1, MedicineId, "Paracetamol 500 mg", 1m, 2m, 1m, 0m, 5m, []));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void A_line_must_sell_a_positive_quantity(int quantity)
        => Assert.Throws<DomainValidationException>(() => Line(quantity: quantity));

    [Fact]
    public void A_discount_cannot_exceed_the_line_value()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => Line(quantity: 1m, unitPrice: 5m, discountAmount: 5.01m));

        Assert.Contains("cannot exceed the line value", exception.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(-0.5)]
    [InlineData(100.01)]
    public void A_tax_rate_must_be_a_percentage(decimal rate)
        => Assert.Throws<DomainValidationException>(() => Line(taxRatePercent: rate));

    [Fact]
    public void The_totals_are_the_sum_of_the_rounded_lines()
    {
        var sale = Invoice(
            Line(quantity: 3m, unitPrice: 3.33m, unitCost: 2m, taxRatePercent: 5m),
            Line(lineNumber: 2, quantity: 1m, unitPrice: 10.00m, unitCost: 6m, discountAmount: 1.25m, taxRatePercent: 12m));

        // Line 1: 9.99 gross, 0.50 tax, 10.49 total, 6.00 cost.
        Assert.Equal(9.99m, sale.Lines.First().GrossAmount);
        Assert.Equal(0.50m, sale.Lines.First().TaxAmount);

        // Line 2: 10.00 gross less 1.25 discount, 1.05 tax, 9.80 total, 6.00 cost.
        Assert.Equal(8.75m, sale.Lines.Last().TaxableAmount);
        Assert.Equal(1.05m, sale.Lines.Last().TaxAmount);

        Assert.Equal(19.99m, sale.SubTotal);
        Assert.Equal(1.25m, sale.DiscountTotal);
        Assert.Equal(18.74m, sale.TaxableTotal);
        Assert.Equal(1.55m, sale.TaxTotal);
        Assert.Equal(20.29m, sale.Total);
        Assert.Equal(12.00m, sale.CostTotal);
        Assert.Equal(6.74m, sale.Margin);
        Assert.Equal(4m, sale.UnitsSold);
        Assert.Equal(0m, sale.UnitsReturned);
        Assert.Equal(0.00m, sale.RefundedTotal);
        Assert.Equal(SaleStatus.Completed, sale.Status);
        Assert.Equal(Now, sale.SoldAtUtc);
        Assert.Equal(BusinessDate, sale.BusinessDate);
    }

    [Fact]
    public void The_payments_must_settle_the_invoice_exactly()
    {
        var lines = new[] { Line() };
        var total = SaleTotals.From(lines).Total;

        var short_ = Assert.Throws<DomainValidationException>(() => Sale.Complete(
            "INV-1", BusinessDate, "asha", lines, [Payment.Record(PaymentMethod.Cash, total - 0.01m, nowUtc: Now)], nowUtc: Now));
        Assert.Contains("do not settle", short_.Message, StringComparison.Ordinal);

        var sale = Sale.Complete(
            "INV-1",
            BusinessDate,
            "asha",
            lines,
            [Payment.Record(PaymentMethod.Cash, 10m, nowUtc: Now), Payment.Record(PaymentMethod.Upi, total - 10m, nowUtc: Now)],
            nowUtc: Now);

        Assert.Equal(2, sale.Payments.Count);
        Assert.Equal(total, sale.Payments.Sum(payment => payment.Amount));
    }

    [Fact]
    public void An_invoice_needs_a_line_and_a_payment()
    {
        Assert.Throws<DomainValidationException>(
            () => Sale.Complete("INV-1", BusinessDate, "asha", [], [Payment.Record(PaymentMethod.Cash, 1m)], nowUtc: Now));

        Assert.Throws<DomainValidationException>(
            () => Sale.Complete("INV-1", BusinessDate, "asha", [Line()], [], nowUtc: Now));
    }

    [Fact]
    public void Invoice_lines_are_numbered_consecutively_from_one()
        => Assert.Throws<DomainValidationException>(() => Invoice(Line(lineNumber: 1), Line(lineNumber: 3)));

    [Fact]
    public void A_payment_must_be_a_positive_amount_in_a_known_method()
    {
        Assert.Throws<DomainValidationException>(() => Payment.Record(PaymentMethod.Cash, 0m, nowUtc: Now));
        Assert.Throws<DomainValidationException>(() => Payment.Record((PaymentMethod)9, 5m, nowUtc: Now));
    }

    [Fact]
    public void An_invoice_that_has_not_been_fully_returned_cannot_be_cancelled()
    {
        var sale = Invoice(Line());

        var exception = Assert.Throws<DomainValidationException>(() => sale.Cancel("asha", "Keyed in twice.", Now));

        Assert.Contains("still has", exception.Message, StringComparison.Ordinal);
        Assert.Equal(SaleStatus.Completed, sale.Status);
    }

    [Fact]
    public void An_invoice_is_marked_returned_once_every_line_has_come_back()
    {
        var sale = Invoice(Line());

        SalesReturn.Record(
            "RET-2026-000001",
            BusinessDate,
            "asha",
            sale,
            "Customer brought the strip back unopened.",
            [new SalesReturnLineRequest(1, 101, 4m, ReturnDisposition.Restock)],
            new Dictionary<int, MedicineBatch> { [101] = Restockable("B-101") },
            nowUtc: Now);

        Assert.True(sale.IsFullyReturned);
        Assert.Equal(SaleStatus.Returned, sale.Status);
        Assert.Equal(4m, sale.UnitsReturned);
        Assert.Equal(31.50m, sale.RefundedTotal);

        // A cancelled invoice still knows what was charged for it: cancellation is a status, not an erasure.
        sale.Cancel("asha", "Wrong medicine dispensed.", Now);

        Assert.Equal(SaleStatus.Cancelled, sale.Status);
        Assert.Equal("asha", sale.CancelledBy);
        Assert.Equal("Wrong medicine dispensed.", sale.CancellationReason);
        Assert.Equal(Now, sale.CancelledAtUtc);
        Assert.Equal(31.50m, sale.Total);
    }

    [Fact]
    public void A_cancelled_invoice_cannot_be_cancelled_again()
    {
        var sale = Invoice(Line());
        SalesReturn.Record(
            "RET-2026-000001",
            BusinessDate,
            "asha",
            sale,
            "Customer brought the strip back unopened.",
            [new SalesReturnLineRequest(1, 101, 4m, ReturnDisposition.Restock)],
            new Dictionary<int, MedicineBatch> { [101] = Restockable("B-101") },
            nowUtc: Now);

        sale.Cancel("asha", "Wrong medicine dispensed.", Now);

        Assert.Throws<DomainValidationException>(() => sale.Cancel("asha", "Again.", Now));
    }

    [Fact]
    public void An_invoice_with_no_lines_loaded_is_not_treated_as_fully_returned()
    {
        // What an unloaded invoice looks like. Reporting it as fully returned would let a cancellation
        // through while the stock is in fact still out with the customer.
        var sale = Invoice(Line());

        var lines = sale.Lines.ToList();
        foreach (var line in lines)
        {
            sale.Lines.Remove(line);
        }

        Assert.Empty(sale.Lines);
        Assert.False(sale.IsFullyReturned);
        Assert.Throws<DomainValidationException>(() => sale.Cancel("asha", "Keyed in twice.", Now));
    }

    [Fact]
    public void An_invoice_needs_an_operator_and_a_number()
    {
        var lines = new[] { Line() };
        var settlement = new[] { Payment.Record(PaymentMethod.Cash, SaleTotals.From(lines).Total, nowUtc: Now) };

        Assert.Throws<DomainValidationException>(
            () => Sale.Complete("INV-1", BusinessDate, "  ", lines, settlement, nowUtc: Now));
        Assert.Throws<DomainValidationException>(
            () => Sale.Complete("   ", BusinessDate, "asha", lines, settlement, nowUtc: Now));
    }

    internal static MedicineBatch Restockable(
        string batchNumber,
        DateOnly? expiry = null,
        decimal quantity = 100m,
        DateOnly? receivedOn = null)
        => MedicineBatch.Receive(
            MedicineId,
            batchNumber,
            expiry ?? Expiry,
            quantity,
            4.50m,
            7.50m,
            receivedOn ?? BusinessDate.AddDays(-400),
            nowUtc: Now);
}
