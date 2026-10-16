using MediStock.Domain.Common;
using MediStock.Domain.Inventory;
using MediStock.Domain.Suppliers;

namespace MediStock.Domain.Tests.Suppliers;

public class SupplierReturnTests
{
    private const int FirstBatch = 101;
    private const int SecondBatch = 102;

    private static readonly DateOnly Today = new(2026, 4, 1);
    private static readonly DateTimeOffset Now = new(2026, 4, 1, 9, 0, 0, TimeSpan.Zero);

    private static MedicineBatch Batch(
        decimal quantity = 100m,
        int? supplierId = 1,
        decimal cost = 1.10m,
        string batchNumber = "PCM2411")
        => MedicineBatch.Receive(
            medicineId: 1,
            batchNumber: batchNumber,
            expiryDate: Today.AddMonths(6),
            quantity: quantity,
            purchaseCost: cost,
            sellingPrice: 2.00m,
            receivedOn: Today.AddDays(-7),
            supplierId: supplierId,
            supplierInvoiceNumber: "INV-1",
            nowUtc: Now);

    private static Dictionary<int, MedicineBatch> Catalogue(params MedicineBatch[] batches)
        => batches.Select((batch, index) => (id: FirstBatch + index, batch))
            .ToDictionary(entry => entry.id, entry => entry.batch);

    private static SupplierReturn Record(
        IReadOnlyList<SupplierReturnLineRequest> lines,
        IReadOnlyDictionary<int, MedicineBatch> batches,
        int supplierId = 1,
        string reason = "Expired stock returned for credit.",
        string? creditNote = null)
        => SupplierReturn.Record(
            "SRET-2026-000001",
            Today,
            "operator",
            supplierId,
            "Medi Wholesale",
            reason,
            lines,
            batches,
            supplierCreditNoteNumber: creditNote,
            nowUtc: Now);

    [Fact]
    public void Record_sends_stock_back_and_costs_the_line_from_the_batch()
    {
        var batch = Batch(quantity: 100m, cost: 1.10m);
        var supplierReturn = Record(
            [new SupplierReturnLineRequest(FirstBatch, 10m)],
            Catalogue(batch));

        Assert.Equal(90m, batch.QuantityOnHand);
        Assert.Equal(BatchStatus.Active, batch.Status);

        var line = Assert.Single(supplierReturn.Lines);
        Assert.Equal(1, line.LineNumber);
        Assert.Equal(FirstBatch, line.MedicineBatchId);
        Assert.Equal(10m, line.Quantity);
        Assert.Equal(1.10m, line.UnitCost);
        Assert.Equal(11.00m, line.CostValue);
        Assert.Equal(11.00m, supplierReturn.TotalCostValue);
        Assert.Equal(10m, supplierReturn.ReturnedQuantity);
        Assert.Equal("SRET-2026-000001", supplierReturn.ReturnNumber);
    }

    [Fact]
    public void Record_numbers_lines_in_order_and_totals_them()
    {
        var first = Batch(batchNumber: "PCM2411");
        var second = Batch(batchNumber: "PCM2502", cost: 2.50m);

        var supplierReturn = Record(
        [
            new SupplierReturnLineRequest(FirstBatch, 10m),
            new SupplierReturnLineRequest(SecondBatch, 4m, "Damaged outer box."),
        ],
            Catalogue(first, second));

        Assert.Equal(2, supplierReturn.Lines.Count);
        Assert.Equal(1, supplierReturn.Lines.First().LineNumber);
        Assert.Equal(2, supplierReturn.Lines.Skip(1).Single().LineNumber);
        Assert.Equal("Damaged outer box.", supplierReturn.Lines.Skip(1).Single().Notes);
        Assert.Equal(11.00m + 10.00m, supplierReturn.TotalCostValue);
    }

    [Fact]
    public void Record_refuses_a_batch_that_was_bought_elsewhere()
    {
        var batch = Batch(supplierId: 2);

        var exception = Assert.Throws<DomainValidationException>(
            () => Record([new SupplierReturnLineRequest(FirstBatch, 10m)], Catalogue(batch)));

        Assert.Contains("was not bought from Medi Wholesale", exception.Message, StringComparison.Ordinal);
        Assert.Equal(100m, batch.QuantityOnHand);
    }

    [Fact]
    public void Record_refuses_a_batch_that_never_had_a_supplier()
    {
        var batch = Batch(supplierId: null);

        Assert.Throws<DomainValidationException>(
            () => Record([new SupplierReturnLineRequest(FirstBatch, 10m)], Catalogue(batch)));
    }

    [Theory]
    [InlineData(BatchStatus.Recalled)]
    [InlineData(BatchStatus.WrittenOff)]
    public void Record_refuses_stock_that_cannot_be_moved(BatchStatus status)
    {
        var batch = Batch();
        batch.WriteOff("Blocked.", status, Now);

        var exception = Assert.Throws<DomainValidationException>(
            () => Record([new SupplierReturnLineRequest(FirstBatch, 10m)], Catalogue(batch)));

        Assert.Contains("cannot be returned to the supplier", exception.Message, StringComparison.Ordinal);
        Assert.Equal(100m, batch.QuantityOnHand);
    }

    [Fact]
    public void Record_counts_repeated_lines_against_the_same_batch()
    {
        var batch = Batch(quantity: 100m);

        var exception = Assert.Throws<DomainValidationException>(
            () => Record(
            [
                new SupplierReturnLineRequest(FirstBatch, 60m),
                new SupplierReturnLineRequest(FirstBatch, 60m),
            ],
                Catalogue(batch)));

        Assert.Contains("holding 100", exception.Message, StringComparison.Ordinal);
        Assert.Equal(100m, batch.QuantityOnHand);
    }

    [Fact]
    public void Record_refuses_more_than_the_batch_holds()
    {
        var batch = Batch(quantity: 100m);

        Assert.Throws<DomainValidationException>(
            () => Record([new SupplierReturnLineRequest(FirstBatch, 100.5m)], Catalogue(batch)));
    }

    [Fact]
    public void Record_empties_the_batch_to_depleted()
    {
        var batch = Batch(quantity: 100m);

        Record([new SupplierReturnLineRequest(FirstBatch, 100m)], Catalogue(batch));

        Assert.Equal(0m, batch.QuantityOnHand);
        Assert.Equal(BatchStatus.Depleted, batch.Status);
    }

    [Fact]
    public void Record_refuses_a_batch_that_could_not_be_loaded()
    {
        var batch = Batch();

        Assert.Throws<DomainValidationException>(
            () => Record([new SupplierReturnLineRequest(FirstBatch + 1, 10m)], Catalogue(batch)));
    }

    [Fact]
    public void Record_requires_a_reason()
    {
        var batch = Batch();

        Assert.Throws<DomainValidationException>(
            () => Record([new SupplierReturnLineRequest(FirstBatch, 10m)], Catalogue(batch), reason: "   "));
    }

    [Fact]
    public void Record_requires_a_named_supplier()
    {
        var batch = Batch();

        Assert.Throws<DomainValidationException>(
            () => Record([new SupplierReturnLineRequest(FirstBatch, 10m)], Catalogue(batch), supplierId: 0));
    }

    [Fact]
    public void Record_requires_at_least_one_quantity()
    {
        var batch = Batch();

        Assert.Throws<DomainValidationException>(() => Record([], Catalogue(batch)));
    }

    [Fact]
    public void Record_keeps_the_suppliers_credit_note_when_one_has_arrived()
    {
        var batch = Batch();

        var supplierReturn = Record(
            [new SupplierReturnLineRequest(FirstBatch, 10m)],
            Catalogue(batch),
            creditNote: "CN-1188");

        Assert.Equal("CN-1188", supplierReturn.SupplierCreditNoteNumber);
    }
}