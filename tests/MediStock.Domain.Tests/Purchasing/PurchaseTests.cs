using MediStock.Domain.Common;
using MediStock.Domain.Inventory;
using MediStock.Domain.Purchasing;

namespace MediStock.Domain.Tests.Purchasing;

/// <summary>
/// The lifecycle rules of a supplier invoice: draft, received, reversed. These are the rules that keep
/// stock from being created twice and keep a correction traceable instead of erasing the original.
/// </summary>
public class PurchaseTests
{
    private const int SupplierId = 7;
    private const int MedicineId = 11;

    private static readonly DateTimeOffset Now = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);
    private static readonly DateOnly InvoiceDate = new(2026, 4, 1);
    private static readonly DateOnly ReceivedOn = new(2026, 4, 6);
    private static readonly DateOnly Expiry = new(2028, 6, 30);

    private static Purchase NewDraft(string invoiceNumber = "SUN-1001", int supplierId = SupplierId)
        => Purchase.Create(supplierId, invoiceNumber, InvoiceDate, nowUtc: Now);

    private static Purchase NewDraftWithLine(decimal quantity = 10m, decimal unitCost = 12.50m)
    {
        var purchase = NewDraft();
        purchase.AddLine(MedicineId, "B-7781", Expiry, quantity, unitCost, 18.00m, Now);
        return purchase;
    }

    [Fact]
    public void Create_requires_an_existing_supplier()
        => Assert.Throws<DomainValidationException>(() => Purchase.Create(0, "SUN-1001", InvoiceDate, nowUtc: Now));

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Create_requires_an_invoice_number(string? invoiceNumber)
        => Assert.Throws<DomainValidationException>(() => Purchase.Create(SupplierId, invoiceNumber!, InvoiceDate, nowUtc: Now));

    [Fact]
    public void Create_rejects_an_over_long_invoice_number()
        => Assert.Throws<DomainValidationException>(() => Purchase.Create(
            SupplierId, new string('X', Purchase.InvoiceNumberMaxLength + 1), InvoiceDate, nowUtc: Now));

    [Fact]
    public void Create_starts_a_draft_with_no_lines_and_nothing_received()
    {
        var purchase = NewDraft("  SUN-1001  ");

        Assert.Equal("SUN-1001", purchase.InvoiceNumber);
        Assert.Equal(PurchaseStatus.Draft, purchase.Status);
        Assert.Empty(purchase.Lines);
        Assert.Equal(0m, purchase.TotalValue);
        Assert.True(purchase.IsEditable);
        Assert.False(purchase.CanReceive);
        Assert.False(purchase.CanReverse);
        Assert.False(purchase.HasStockOnHand);
        Assert.Null(purchase.ReceivedOn);
        Assert.Null(purchase.ReceivedBy);
        Assert.Null(purchase.ClosedAtUtc);
        Assert.Equal(Now, purchase.CreatedAtUtc);
        Assert.Equal(Now, purchase.UpdatedAtUtc);
    }

    [Fact]
    public void UpdateHeader_rewrites_the_header_while_the_purchase_is_a_draft()
    {
        var purchase = NewDraft();
        var later = Now.AddHours(2);

        purchase.UpdateHeader(9, "  EDH-77 ", new DateOnly(2026, 4, 2), "  part delivery  ", later);

        Assert.Equal(9, purchase.SupplierId);
        Assert.Equal("EDH-77", purchase.InvoiceNumber);
        Assert.Equal(new DateOnly(2026, 4, 2), purchase.InvoiceDate);
        Assert.Equal("part delivery", purchase.Notes);
        Assert.Equal(later, purchase.UpdatedAtUtc);
    }

    [Fact]
    public void UpdateHeader_rejects_an_unknown_supplier()
    {
        var purchase = NewDraft();

        Assert.Throws<DomainValidationException>(() => purchase.UpdateHeader(0, "SUN-1001", InvoiceDate, null, Now));
    }

    [Fact]
    public void AddLine_adds_the_line_and_moves_the_timestamp()
    {
        var purchase = NewDraft();
        var later = Now.AddMinutes(5);

        var line = purchase.AddLine(MedicineId, " B-7781 ", Expiry, 10m, 12.50m, 18m, later);

        var stored = Assert.Single(purchase.Lines);
        Assert.Same(line, stored);
        Assert.Equal("B-7781", line.BatchNumber);
        Assert.Equal(10m, line.Quantity);
        Assert.Equal(0m, line.QuantityReceived);
        Assert.Equal(Expiry, line.ExpiryDate);
        Assert.True(purchase.CanReceive);
        Assert.Equal(later, purchase.UpdatedAtUtc);
    }

    [Fact]
    public void AddLine_rejects_a_second_line_for_the_same_medicine_and_batch()
    {
        var purchase = NewDraftWithLine();

        var error = Assert.Throws<DomainValidationException>(
            () => purchase.AddLine(MedicineId, "b-7781", Expiry, 5m, 12.50m, 18m, Now));

        Assert.Contains("Combine the quantities", error.Message, StringComparison.Ordinal);
        Assert.Single(purchase.Lines);
    }

    [Fact]
    public void AddLine_allows_the_same_batch_number_for_a_different_medicine()
    {
        var purchase = NewDraftWithLine();

        purchase.AddLine(MedicineId + 1, "B-7781", Expiry, 5m, 9.00m, 14m, Now);

        Assert.Equal(2, purchase.Lines.Count);
    }

    [Fact]
    public void RemoveLine_removes_a_draft_line()
    {
        var purchase = NewDraftWithLine();
        var line = purchase.Lines.First();

        purchase.RemoveLine(line.Id, Now.AddMinutes(1));

        Assert.Empty(purchase.Lines);
        Assert.False(purchase.CanReceive);
        Assert.Equal(Now.AddMinutes(1), purchase.UpdatedAtUtc);
    }

    [Fact]
    public void RemoveLine_rejects_a_line_that_is_not_on_the_purchase()
    {
        var purchase = NewDraftWithLine();

        Assert.Throws<DomainValidationException>(() => purchase.RemoveLine(4321, Now));
    }

    [Fact]
    public void Receive_requires_at_least_one_line()
    {
        var purchase = NewDraft();

        var error = Assert.Throws<DomainValidationException>(() => purchase.Receive(ReceivedOn, "imran", Now));

        Assert.Contains("at least one line", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Receive_refuses_stock_that_has_already_expired()
    {
        var purchase = NewDraft();
        purchase.AddLine(MedicineId, "B-7781", ReceivedOn, 10m, 12.50m, 18m, Now);

        var error = Assert.Throws<DomainValidationException>(() => purchase.Receive(ReceivedOn, "imran", Now));

        Assert.Contains("expires on or before", error.Message, StringComparison.Ordinal);
        Assert.Equal(PurchaseStatus.Draft, purchase.Status);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void Receive_requires_an_operator(string? receivedBy)
    {
        var purchase = NewDraftWithLine();

        Assert.Throws<DomainValidationException>(() => purchase.Receive(ReceivedOn, receivedBy!, Now));
        Assert.Equal(PurchaseStatus.Draft, purchase.Status);
    }

    [Fact]
    public void Receive_takes_the_purchase_into_stock_and_reports_what_to_create()
    {
        var purchase = NewDraftWithLine(quantity: 10m, unitCost: 12.50m);
        purchase.AddLine(MedicineId, "B-7782", Expiry, 4m, 30.00m, 44m, Now);
        var receivedAt = Now.AddDays(5);

        var receipts = purchase.Receive(ReceivedOn, " imran ", receivedAt);

        Assert.Equal(PurchaseStatus.Received, purchase.Status);
        Assert.Equal(ReceivedOn, purchase.ReceivedOn);
        Assert.Equal("imran", purchase.ReceivedBy);
        Assert.Equal(receivedAt, purchase.ReceivedAtUtc);
        Assert.Equal(receivedAt, purchase.UpdatedAtUtc);
        Assert.True(purchase.HasStockOnHand);
        Assert.True(purchase.CanReverse);
        Assert.False(purchase.CanReceive);
        Assert.False(purchase.IsEditable);

        Assert.Equal(2, receipts.Count);
        var first = receipts[0];
        Assert.Equal(MedicineId, first.MedicineId);
        Assert.Equal("B-7781", first.BatchNumber);
        Assert.Equal(Expiry, first.ExpiryDate);
        Assert.Equal(10m, first.Quantity);
        Assert.Equal(12.50m, first.UnitCost);
        Assert.Equal(18m, first.SellingPrice);

        Assert.All(purchase.Lines, line => Assert.Equal(line.Quantity, line.QuantityReceived));
    }

    [Fact]
    public void Receive_cannot_be_repeated()
    {
        var purchase = NewDraftWithLine();
        purchase.Receive(ReceivedOn, "imran", Now);

        var error = Assert.Throws<DomainValidationException>(() => purchase.Receive(ReceivedOn, "imran", Now.AddMinutes(1)));

        Assert.Contains("Only a draft purchase", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void A_received_purchase_can_no_longer_be_edited()
    {
        var purchase = NewDraftWithLine();
        var lineId = purchase.Lines.First().Id;
        purchase.Receive(ReceivedOn, "imran", Now);

        Assert.Throws<DomainValidationException>(() => purchase.AddLine(MedicineId + 1, "B-9", Expiry, 1m, 1m, 1m, Now));
        Assert.Throws<DomainValidationException>(() => purchase.RemoveLine(lineId, Now));
        Assert.Throws<DomainValidationException>(() => purchase.UpdateHeader(SupplierId, "SUN-2", InvoiceDate, null, Now));

        Assert.Single(purchase.Lines);
    }

    [Fact]
    public void Cancel_abandons_a_draft_before_any_stock_exists()
    {
        var purchase = NewDraftWithLine();
        var cancelledAt = Now.AddHours(3);

        purchase.Cancel(" supplier invoice withdrawn ", " imran ", cancelledAt);

        Assert.Equal(PurchaseStatus.Cancelled, purchase.Status);
        Assert.Equal("supplier invoice withdrawn", purchase.StatusReason);
        Assert.Equal("imran", purchase.ClosedBy);
        Assert.Equal(cancelledAt, purchase.ClosedAtUtc);
        Assert.Equal(cancelledAt, purchase.UpdatedAtUtc);
        Assert.False(purchase.IsEditable);
        Assert.False(purchase.CanReceive);
        Assert.False(purchase.CanReverse);
        Assert.False(purchase.HasStockOnHand);
    }

    [Fact]
    public void Cancel_requires_a_reason()
    {
        var purchase = NewDraftWithLine();

        Assert.Throws<DomainValidationException>(() => purchase.Cancel("  ", "imran", Now));
        Assert.Equal(PurchaseStatus.Draft, purchase.Status);
    }

    [Fact]
    public void Cancel_cannot_be_repeated()
    {
        var purchase = NewDraftWithLine();
        purchase.Cancel("wrong supplier", "imran", Now);

        Assert.Throws<DomainValidationException>(() => purchase.Cancel("again", "imran", Now.AddMinutes(1)));
    }

    [Fact]
    public void Reverse_requires_a_received_purchase()
    {
        var purchase = NewDraftWithLine();

        var error = Assert.Throws<DomainValidationException>(() => purchase.Reverse("wrong invoice", "imran", Now));

        Assert.Contains("Only a received purchase", error.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Reverse_undoes_a_receipt_and_records_why()
    {
        var purchase = NewDraftWithLine();
        purchase.Receive(ReceivedOn, "imran", Now);
        var reversedAt = Now.AddDays(1);

        purchase.Reverse(" goods damaged in transit ", " imran ", reversedAt);

        Assert.Equal(PurchaseStatus.Reversed, purchase.Status);
        Assert.Equal("goods damaged in transit", purchase.StatusReason);
        Assert.Equal("imran", purchase.ClosedBy);
        Assert.Equal(reversedAt, purchase.ClosedAtUtc);
        Assert.Equal(reversedAt, purchase.UpdatedAtUtc);
        Assert.False(purchase.CanReverse);
        Assert.False(purchase.HasStockOnHand);
    }

    [Fact]
    public void Reverse_requires_a_reason()
    {
        var purchase = NewDraftWithLine();
        purchase.Receive(ReceivedOn, "imran", Now);

        Assert.Throws<DomainValidationException>(() => purchase.Reverse("", "imran", Now));
        Assert.Equal(PurchaseStatus.Received, purchase.Status);
    }

    [Fact]
    public void Reverse_cannot_be_repeated()
    {
        var purchase = NewDraftWithLine();
        purchase.Receive(ReceivedOn, "imran", Now);
        purchase.Reverse("damaged", "imran", Now);

        Assert.Throws<DomainValidationException>(() => purchase.Reverse("again", "imran", Now.AddMinutes(1)));
    }

    [Fact]
    public void A_reversed_purchase_keeps_its_lines_for_the_audit_trail()
    {
        var purchase = NewDraftWithLine();
        purchase.Receive(ReceivedOn, "imran", Now);

        purchase.Reverse("damaged", "imran", Now.AddDays(1));

        var line = Assert.Single(purchase.Lines);
        Assert.Equal(10m, line.QuantityReceived);
        Assert.Equal(125.00m, purchase.TotalValue);
    }

    [Fact]
    public void LinkedBatches_is_empty_until_the_batches_have_been_created()
    {
        var purchase = NewDraftWithLine();
        purchase.Receive(ReceivedOn, "imran", Now);

        Assert.Empty(purchase.LinkedBatches());
    }

    [Fact]
    public void AttachBatch_refuses_a_batch_that_has_not_been_saved()
    {
        var purchase = NewDraftWithLine();
        var line = purchase.Lines.First();
        purchase.Receive(ReceivedOn, "imran", Now);

        var unsavedBatch = MedicineBatch.Receive(
            MedicineId, "B-7781", Expiry, 10m, 12.50m, 18m, ReceivedOn, SupplierId, "SUN-1001", Now);

        Assert.Throws<InvalidOperationException>(() => purchase.AttachBatch(line.Id, unsavedBatch));
    }

    [Fact]
    public void AttachBatch_rejects_a_line_that_is_not_on_the_purchase()
    {
        var purchase = NewDraftWithLine();

        Assert.Throws<DomainValidationException>(() => purchase.AttachBatch(9999, null!));
    }
}
