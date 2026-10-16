using MediStock.Domain.Common;
using MediStock.Domain.Inventory;
using MediStock.Domain.Sales;

namespace MediStock.Domain.Tests.Sales;

/// <summary>
/// The rules of a customer return: what may come back, how much of it, what is refunded, and where the
/// returned goods go. These are the rules that keep a refund from exceeding what was charged and keep
/// returned medicine out of sellable stock unless someone says otherwise.
/// </summary>
public class SalesReturnTests
{
    private const int MedicineId = 11;
    private const int FirstBatch = 101;
    private const int SecondBatch = 102;
    private const int InvoiceId = 900;
    private const int SaleLineId = 501;

    private static readonly DateOnly BusinessDate = new(2026, 5, 4);
    private static readonly DateTimeOffset Now = new(2026, 5, 4, 11, 30, 0, TimeSpan.Zero);

    /// <summary>A four-unit line at 7.50 with five percent tax: 30.00 gross, 1.50 tax, 31.50 charged, 20.00 cost.</summary>
    private static SaleLine Line(params (int BatchId, decimal Quantity)[] batches)
    {
        var allocations = batches.Length == 0
            ? new List<BatchAllocation> { new(FirstBatch, "B-101", BusinessDate.AddYears(2), 4m) }
            : new List<BatchAllocation>(batches.Select(batch => new BatchAllocation(
                batch.BatchId,
                $"B-{batch.BatchId}",
                BusinessDate.AddYears(2),
                batch.Quantity)));

        return SaleLine.Create(1, MedicineId, "Paracetamol 500 mg", 4m, 7.50m, 5.00m, 0m, 5m, allocations);
    }

    private static Sale Invoice(SaleLine? line = null)
    {
        var lines = new[] { (line ?? Line()).WithId(SaleLineId) };

        return Sale
            .Complete(
                "INV-2026-000042",
                BusinessDate,
                "asha",
                lines,
                [Payment.Record(PaymentMethod.Cash, SaleTotals.From(lines).Total, nowUtc: Now)],
                nowUtc: Now)
            .WithId(InvoiceId);
    }

    private static SalesReturn Apply(
        Sale sale,
        IReadOnlyList<SalesReturnLineRequest> lines,
        IReadOnlyDictionary<int, MedicineBatch>? batches = null,
        string reason = "Customer brought the strip back unopened.",
        DateOnly? businessDate = null,
        PaymentMethod refundMethod = PaymentMethod.Cash,
        string? notes = null)
        => SalesReturn.Record(
            "RET-2026-000001",
            businessDate ?? BusinessDate,
            "asha",
            sale,
            reason,
            lines,
            batches ?? new Dictionary<int, MedicineBatch> { [FirstBatch] = SaleTests.Restockable("B-101") },
            notes,
            refundMethod,
            Now);

    [Fact]
    public void A_return_must_return_something()
        => Assert.Throws<DomainValidationException>(() => Apply(Invoice(), []));

    [Fact]
    public void A_return_must_name_a_line_of_the_invoice()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => Apply(Invoice(), [new SalesReturnLineRequest(2, FirstBatch, 1m, ReturnDisposition.Restock)]));

        Assert.Contains("has no line 2", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_return_must_name_a_batch_that_the_line_sold()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => Apply(
                Invoice(),
                [new SalesReturnLineRequest(1, 999, 1m, ReturnDisposition.Restock)],
                new Dictionary<int, MedicineBatch> { [999] = SaleTests.Restockable("B-999") }));

        Assert.Contains("did not sell batch 999", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_return_must_be_able_to_cost_the_batch_it_credits()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => Apply(
                Invoice(),
                [new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Restock)],
                new Dictionary<int, MedicineBatch>()));

        Assert.Contains("could not be loaded", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_return_cannot_exceed_what_is_left_on_the_line()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => Apply(Invoice(), [new SalesReturnLineRequest(1, FirstBatch, 5m, ReturnDisposition.Restock)]));

        Assert.Contains("at most", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_return_cannot_exceed_what_is_left_in_the_batch()
    {
        var exception = Assert.Throws<DomainValidationException>(
            () => Apply(Invoice(Line((FirstBatch, 2m), (SecondBatch, 2m))), [new SalesReturnLineRequest(1, FirstBatch, 3m, ReturnDisposition.Restock)]));

        Assert.Contains("accounted for 2 of line 1", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Two_entries_in_one_return_may_not_add_up_to_more_than_the_line()
    {
        // Neither entry is too large on its own; together they ask for five of the four that were sold.
        var exception = Assert.Throws<DomainValidationException>(() => Apply(
            Invoice(),
            [
                new SalesReturnLineRequest(1, FirstBatch, 3m, ReturnDisposition.Restock),
                new SalesReturnLineRequest(1, FirstBatch, 2m, ReturnDisposition.Restock),
            ]));

        Assert.Contains("at most", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_rejected_return_leaves_the_invoice_untouched()
    {
        var sale = Invoice();
        var line = sale.Lines.First();

        Assert.Throws<DomainValidationException>(() => Apply(
            sale,
            [
                new SalesReturnLineRequest(1, FirstBatch, 2m, ReturnDisposition.Restock),
                new SalesReturnLineRequest(1, FirstBatch, 3m, ReturnDisposition.Restock),
            ]));

        // Pass one rejected the basket before pass two applied any of it: half a return cannot be
        // reconstructed by the operator afterwards.
        Assert.Equal(0m, line.ReturnedQuantity);
        Assert.Equal(0m, line.ReturnedAmount);
        Assert.Empty(sale.Returns);
        Assert.Equal(SaleStatus.Completed, sale.Status);
    }

    [Fact]
    public void A_partial_return_takes_its_proportional_share_of_the_line()
    {
        var sale = Invoice();
        var line = sale.Lines.First();

        var salesReturn = Apply(sale, [new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Restock)]);

        // A quarter of 31.50 is 7.875 of refund and 0.375 of tax, both rounded once to 7.88 and 0.38.
        Assert.Equal(7.88m, salesReturn.RefundTotal);
        Assert.Equal(7.88m, salesReturn.Lines.Single().RefundAmount);
        Assert.Equal(0.38m, salesReturn.Lines.Single().TaxAmount);
        Assert.Equal(5.00m, salesReturn.Lines.Single().CostAmount);
        Assert.Equal(ReturnDisposition.Restock, salesReturn.Lines.Single().Disposition);
        Assert.Equal(1, salesReturn.Lines.Single().LineNumber);
        Assert.Equal(FirstBatch, salesReturn.Lines.Single().MedicineBatchId);

        Assert.Equal(1m, line.ReturnedQuantity);
        Assert.Equal(7.88m, line.ReturnedAmount);
        Assert.Equal(3m, line.ReturnableQuantity);
        Assert.Equal(23.62m, line.ReturnableAmount);
        Assert.Equal(1.12m, line.ReturnableTax);
        Assert.Equal(15.00m, line.ReturnableCost);
        Assert.False(line.IsFullyReturned);

        Assert.Equal(1m, salesReturn.ReturnedQuantity);
        Assert.Equal(1m, salesReturn.RestockedQuantity);
        Assert.Equal(1m, sale.UnitsReturned);
        Assert.Equal(SaleStatus.Completed, sale.Status);
    }

    [Fact]
    public void The_entry_that_clears_a_line_refunds_exactly_what_was_charged_on_it()
    {
        var sale = Invoice();
        var line = sale.Lines.First();

        Apply(sale, [new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Restock)]);
        var clearing = Apply(sale, [new SalesReturnLineRequest(1, FirstBatch, 3m, ReturnDisposition.Restock)]);

        // 31.50 less the 7.88 already refunded, and the same for the tax and the cost: the parts add
        // back up to the whole to the paisa, which is what "fully returned" has to mean.
        Assert.Equal(23.62m, clearing.Lines.Single().RefundAmount);
        Assert.Equal(1.12m, clearing.Lines.Single().TaxAmount);
        Assert.Equal(15.00m, clearing.Lines.Single().CostAmount);

        Assert.Equal(31.50m, line.ReturnedAmount);
        Assert.Equal(1.50m, line.ReturnedTax);
        Assert.Equal(20.00m, line.ReturnedCost);
        Assert.True(line.IsFullyReturned);
        Assert.Equal(SaleStatus.Returned, sale.Status);
        Assert.Equal(31.50m, sale.RefundedTotal);
        Assert.Equal(31.50m, sale.Returns.Sum(entry => entry.RefundTotal));
    }

    [Fact]
    public void A_return_may_span_the_batches_the_line_sold()
    {
        var sale = Invoice(Line((FirstBatch, 2m), (SecondBatch, 2m)));

        var salesReturn = Apply(
            sale,
            [
                new SalesReturnLineRequest(1, FirstBatch, 2m, ReturnDisposition.Restock),
                new SalesReturnLineRequest(1, SecondBatch, 2m, ReturnDisposition.Restock),
            ],
            new Dictionary<int, MedicineBatch>
            {
                [FirstBatch] = SaleTests.Restockable("B-101"),
                [SecondBatch] = SaleTests.Restockable("B-102"),
            });

        Assert.Equal(2, salesReturn.Lines.Count);
        Assert.Equal(1, salesReturn.Lines.First().LineNumber);
        Assert.Equal(2, salesReturn.Lines.Last().LineNumber);
        Assert.Equal(15.75m, salesReturn.Lines.First().RefundAmount);
        Assert.Equal(15.75m, salesReturn.Lines.Last().RefundAmount);
        Assert.Equal(31.50m, salesReturn.RefundTotal);
        Assert.Equal(4m, salesReturn.RestockedQuantity);
        Assert.True(sale.IsFullyReturned);

        // Each batch remembers its own share, so a later return cannot credit it twice.
        var allocations = sale.Lines.First().Allocations.OrderBy(allocation => allocation.MedicineBatchId).ToList();
        Assert.Equal(2m, allocations.First().ReturnedQuantity);
        Assert.Equal(0m, allocations.First().ReturnableQuantity);
        Assert.Equal(2m, allocations.Last().ReturnedQuantity);
    }

    [Fact]
    public void Returned_medicine_is_only_restocked_when_the_operator_says_so()
    {
        var sale = Invoice();
        var batch = SaleTests.Restockable("B-101");
        batch.DecreaseQuantity(4m, Now);

        var salesReturn = Apply(
            sale,
            [
                new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Quarantine, "Seal broken."),
                new SalesReturnLineRequest(1, FirstBatch, 3m, ReturnDisposition.Destroy, "Water damage."),
            ],
            new Dictionary<int, MedicineBatch> { [FirstBatch] = batch });

        // The quantities are recorded, and the money is refunded, but nothing goes back on the shelf.
        Assert.Equal(4m, salesReturn.ReturnedQuantity);
        Assert.Equal(0m, salesReturn.RestockedQuantity);
        Assert.Equal(31.50m, salesReturn.RefundTotal);
        Assert.Equal("Seal broken.", salesReturn.Lines.First().Notes);

        // Four units left the shelf and neither disposition puts anything back into the batch.
        Assert.Equal(96m, batch.QuantityOnHand);
        Assert.Equal(100m, batch.QuantityReceived);
        Assert.Equal(SaleStatus.Returned, sale.Status);
    }

    [Fact]
    public void Restocking_is_refused_for_stock_that_can_no_longer_be_sold()
    {
        var sale = Invoice();
        var expired = SaleTests.Restockable("B-101", BusinessDate.AddDays(-1));

        var exception = Assert.Throws<DomainValidationException>(() => Apply(
            sale,
            [new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Restock)],
            new Dictionary<int, MedicineBatch> { [FirstBatch] = expired }));

        Assert.Contains("cannot be restocked", exception.Message, StringComparison.Ordinal);
        Assert.Contains("Quarantine or destroy", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0m, sale.Lines.First().ReturnedQuantity);
    }

    [Theory]
    [InlineData(BatchStatus.Recalled, "it has been recalled")]
    [InlineData(BatchStatus.WrittenOff, "it has been written off")]
    public void A_recalled_or_written_off_batch_cannot_be_restocked(BatchStatus status, string expected)
    {
        var sale = Invoice();
        var batch = SaleTests.Restockable("B-101");
        batch.WriteOff("Withdrawn by the manufacturer.", status, Now);

        var exception = Assert.Throws<DomainValidationException>(() => Apply(
            sale,
            [new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Restock)],
            new Dictionary<int, MedicineBatch> { [FirstBatch] = batch }));

        Assert.Contains(expected, exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Stock_that_cannot_be_restocked_may_still_be_quarantined()
    {
        var sale = Invoice();
        var expired = SaleTests.Restockable("B-101", BusinessDate.AddDays(-1));

        var salesReturn = Apply(
            sale,
            [new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Quarantine)],
            new Dictionary<int, MedicineBatch> { [FirstBatch] = expired });

        Assert.Equal(0m, salesReturn.RestockedQuantity);
        Assert.Equal(7.88m, salesReturn.RefundTotal);
    }

    [Fact]
    public void A_depleted_batch_can_take_its_own_stock_back()
    {
        var sale = Invoice();
        var batch = SaleTests.Restockable("B-101", quantity: 4m);
        batch.DecreaseQuantity(4m, Now);
        Assert.Equal(BatchStatus.Depleted, batch.Status);

        var salesReturn = Apply(sale, [new SalesReturnLineRequest(1, FirstBatch, 4m, ReturnDisposition.Restock)]);

        Assert.Equal(4m, salesReturn.RestockedQuantity);

        // The restock itself is applied by the caller that holds the batch, and it only moves the
        // cached balance: the goods were not bought again, so QuantityReceived must not move with it.
        batch.RestoreQuantity(4m, BusinessDate, Now);

        Assert.Equal(4m, batch.QuantityOnHand);
        Assert.Equal(4m, batch.QuantityReceived);
        Assert.Equal(BatchStatus.Active, batch.Status);
    }

    [Fact]
    public void A_return_may_not_be_dated_before_the_invoice_it_credits()
    {
        var exception = Assert.Throws<DomainValidationException>(() => Apply(
            Invoice(),
            [new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Restock)],
            businessDate: BusinessDate.AddDays(-1)));

        Assert.Contains("before the invoice's business date", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_return_requires_a_reason_and_an_operator()
    {
        Assert.Throws<DomainValidationException>(() => Apply(
            Invoice(),
            [new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Restock)],
            reason: "   "));

        Assert.Throws<DomainValidationException>(() => SalesReturn.Record(
            "RET-2026-000001",
            BusinessDate,
            "  ",
            Invoice(),
            "Unopened pack.",
            [new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Restock)],
            new Dictionary<int, MedicineBatch> { [FirstBatch] = SaleTests.Restockable("B-101") },
            nowUtc: Now));
    }

    [Fact]
    public void A_return_must_say_how_the_money_was_handed_back()
    {
        var exception = Assert.Throws<DomainValidationException>(() => Apply(
            Invoice(),
            [new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Restock)],
            refundMethod: (PaymentMethod)9));

        Assert.Contains("not a valid way to pay a refund back", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_return_records_the_refund_method_and_the_notes()
    {
        var sale = Invoice();

        var salesReturn = Apply(
            sale,
            [new SalesReturnLineRequest(1, FirstBatch, 4m, ReturnDisposition.Restock)],
            refundMethod: PaymentMethod.Upi,
            notes: "Refunded to the customer's UPI handle.");

        Assert.Equal(PaymentMethod.Upi, salesReturn.RefundMethod);
        Assert.Equal("Refunded to the customer's UPI handle.", salesReturn.Notes);
        Assert.Equal("asha", salesReturn.ReturnedBy);
        Assert.Equal(BusinessDate, salesReturn.BusinessDate);
        Assert.Equal("RET-2026-000001", salesReturn.ReturnNumber);
        Assert.Equal(Now, salesReturn.ReturnedAtUtc);
        Assert.Same(sale, salesReturn.Sale);
    }

    [Fact]
    public void A_cancelled_invoice_cannot_take_a_return()
    {
        var sale = Invoice();
        Apply(sale, [new SalesReturnLineRequest(1, FirstBatch, 4m, ReturnDisposition.Restock)]);
        sale.Cancel("asha", "Keyed in twice.", Now);

        var exception = Assert.Throws<DomainValidationException>(() => Apply(
            sale,
            [new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Restock)]));

        Assert.Contains("was cancelled", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void What_a_return_would_refund_is_what_it_does_refund()
    {
        // The counter quotes a figure before it takes the goods back, so the preview and the recorded
        // return have to be the same arithmetic rather than two rules that happen to look alike.
        var line = Line();

        var previewed = SalesReturn.EstimateRefund(line, 1m);
        var recorded = Apply(Invoice(line), [new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Quarantine)]);

        Assert.Equal(7.88m, previewed);
        Assert.Equal(previewed, recorded.Lines.Single().RefundAmount);

        var previewedRest = SalesReturn.EstimateRefund(line, 3m);
        var recordedRest = Apply(
            Invoice(line),
            [new SalesReturnLineRequest(1, FirstBatch, 3m, ReturnDisposition.Quarantine)]);

        Assert.Equal(23.62m, previewedRest);
        Assert.Equal(previewedRest, recordedRest.Lines.Single().RefundAmount);
        Assert.Equal(31.50m, previewed + previewedRest);
    }

    [Fact]
    public void A_line_split_across_entries_shares_its_money_without_losing_a_paisa()
    {
        // Four units of a line charged 31.50, given back one at a time: the shares must add up to the
        // whole, and only the entry that clears the line may take a rounding remainder.
        var line = Line();
        var sale = Invoice(line);

        for (var entry = 1; entry <= 4; entry++)
        {
            Apply(sale, [new SalesReturnLineRequest(1, FirstBatch, 1m, ReturnDisposition.Quarantine)]);
        }

        Assert.Equal(31.50m, line.ReturnedAmount);
        Assert.Equal(line.TaxAmount, line.ReturnedTax);
        Assert.Equal(4m, line.ReturnedQuantity);
    }
}
