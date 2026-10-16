using MediStock.Application.Catalogue;
using MediStock.Application.Inventory;
using MediStock.Application.Purchasing;
using MediStock.Application.Sales;
using MediStock.Application.Suppliers;
using MediStock.Domain.Catalogue;
using MediStock.Domain.Inventory;
using MediStock.Domain.Purchasing;
using MediStock.Domain.Sales;
using MediStock.Infrastructure.Persistence;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Sales;

/// <summary>
/// Customer returns against a real SQL Server database. A return is the only operation that gives money
/// back <em>and</em> puts stock back at the same time, so these tests are about the two things that must
/// never happen: a line refunding more than it charged, and goods going back onto a shelf they should not
/// be sold from. Both are decided inside one transaction, and the transaction is what the tests measure.
/// </summary>
[Collection(TestDatabaseCollection.Name)]
public class SaleReturnCatalogTests
{
    private const string Operator = "integration-test";

    private static DateOnly Today => BusinessTime.Today;

    private readonly TestDatabaseFixture _fixture;

    public SaleReturnCatalogTests(TestDatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// A partial return followed by the rest of the line. The first entry splits the charge in
    /// proportion; the entry that clears the line takes the exact remainder, so the two refunds together
    /// are precisely what the invoice charged, and the shelf ends up holding exactly what it held before
    /// the sale.
    /// </summary>
    [Fact]
    public async Task A_restocked_return_puts_the_units_back_and_refunds_exactly_what_was_charged()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Return test {Unique()}", taxRatePercent: 12m);
            var batchId = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 12.50m, 20.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();
            var returns = services.GetRequiredService<ISaleReturnCatalog>();

            var checkout = await sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                CustomerName = "Returning customer",
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 3m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 67.20m }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            var saleId = checkout.Value!.Id;
            var preview = await returns.PreviewReturnNumberAsync();

            var first = await returns.RecordAsync(new SalesReturnDraft
            {
                SaleId = saleId,
                ReturnedBy = Operator,
                Reason = "Customer changed prescription",
                Lines =
                [
                    new SalesReturnLineDraft
                    {
                        LineNumber = 1,
                        MedicineBatchId = batchId,
                        Quantity = 1m,
                        Disposition = ReturnDisposition.Restock,
                        Notes = "Seal intact",
                    },
                ],
            });

            Assert.True(first.Succeeded, first.Message);

            var second = await returns.RecordAsync(new SalesReturnDraft
            {
                SaleId = saleId,
                ReturnedBy = Operator,
                Reason = "Remainder of the strips",
                Lines =
                [
                    new SalesReturnLineDraft
                    {
                        LineNumber = 1,
                        MedicineBatchId = batchId,
                        Quantity = 2m,
                        Disposition = ReturnDisposition.Restock,
                    },
                ],
            });

            Assert.True(second.Succeeded, second.Message);

            return new
            {
                Preview = preview,
                Sale = await sales.FindAsync(saleId),
                First = first.Value!,
                Second = second.Value!,
                Batch = await BatchAsync(services, batch),
                Movements = await MovementsAsync(services, medicineId),
                Reconciliation = await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync(),
            };
        });

        var sale = outcome.Sale!;
        var line = Assert.Single(sale.Lines);

        // Three strips at 20.00 with 12% tax: 60.00 charged, 7.20 in tax, 67.20 taken at the counter,
        // 37.50 of cost. Every one of those figures comes back in full across the two returns.
        Assert.Equal(67.20m, line.LineTotal);
        Assert.Equal(7.20m, line.TaxAmount);
        Assert.Equal(37.50m, line.LineCost);

        Assert.Equal(22.40m, outcome.First.RefundTotal);
        Assert.Equal(44.80m, outcome.Second.RefundTotal);
        Assert.Equal(line.LineTotal, outcome.First.RefundTotal + outcome.Second.RefundTotal);

        Assert.Equal(3m, line.ReturnedQuantity);
        Assert.Equal(67.20m, line.ReturnedAmount);
        Assert.Equal(67.20m, sale.ReturnedTotal);
        Assert.True(line.IsFullyReturned);
        Assert.True(sale.IsFullyReturned);
        Assert.Equal("Returning customer", outcome.First.CustomerName);

        // The return document carries its own number, taken from the sequence the preview read.
        Assert.StartsWith($"RET-{Today.Year}-", outcome.First.ReturnNumber, StringComparison.Ordinal);
        Assert.NotEqual(outcome.First.ReturnNumber, outcome.Second.ReturnNumber);
        Assert.NotEqual(sale.InvoiceNumber, outcome.First.ReturnNumber);
        Assert.Equal(Today, outcome.First.BusinessDate);
        Assert.Equal(sale.InvoiceNumber, outcome.First.InvoiceNumber);

        // Ten strips arrived, three were sold, three came back: the shelf holds ten again.
        Assert.Equal(10m, outcome.Batch.QuantityOnHand);

        var restocks = outcome.Movements
            .Where(movement => movement.MovementType == StockMovementType.SalesReturn)
            .OrderBy(movement => movement.Id)
            .ToList();

        Assert.Equal(2, restocks.Count);
        Assert.Equal(1m, restocks[0].QuantityDelta);
        Assert.Equal(8m, restocks[0].QuantityAfter);
        Assert.Equal(2m, restocks[1].QuantityDelta);
        Assert.Equal(10m, restocks[1].QuantityAfter);
        Assert.Equal($"RET-{outcome.First.Id}", restocks[0].Reference);
        Assert.Equal($"RET-{outcome.Second.Id}", restocks[1].Reference);

        // A restock is a movement like any other, so the cached balance still equals the ledger sum.
        Assert.True(outcome.Reconciliation.IsReconciled);
    }

    /// <summary>
    /// Medicine a customer has handed back is not sellable just because it is on the premises. Quarantine
    /// and destroy both refund the customer and both leave the shelf exactly as it was.
    /// </summary>
    [Fact]
    public async Task Returned_medicine_that_is_quarantined_or_destroyed_refunds_without_touching_the_shelf()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Disposition test {Unique()}");
            var batchId = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 4.00m, 20.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();
            var returns = services.GetRequiredService<ISaleReturnCatalog>();

            var checkout = await sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 4m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 80.00m }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            var saleId = checkout.Value!.Id;

            // The disposition is not stated, so the safe one applies: quarantined, not restocked.
            var quarantined = await returns.RecordAsync(new SalesReturnDraft
            {
                SaleId = saleId,
                ReturnedBy = Operator,
                Reason = "Customer kept the strips warm",
                RefundMethod = PaymentMethod.Upi,
                Lines =
                [
                    new SalesReturnLineDraft { LineNumber = 1, MedicineBatchId = batchId, Quantity = 1m },
                ],
            });

            Assert.True(quarantined.Succeeded, quarantined.Message);

            var destroyed = await returns.RecordAsync(new SalesReturnDraft
            {
                SaleId = saleId,
                ReturnedBy = Operator,
                Reason = "Damaged packaging",
                Lines =
                [
                    new SalesReturnLineDraft
                    {
                        LineNumber = 1,
                        MedicineBatchId = batchId,
                        Quantity = 1m,
                        Disposition = ReturnDisposition.Destroy,
                    },
                ],
            });

            Assert.True(destroyed.Succeeded, destroyed.Message);

            return new
            {
                Sale = await sales.FindAsync(saleId),
                Before = await BatchAsync(services, batch),
                Movements = await MovementsAsync(services, medicineId),
                Quarantined = quarantined.Value!,
                Destroyed = destroyed.Value!,
                Reconciliation = await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync(),
            };
        });

        // Six of the ten strips are left, and both returned strips are out of sellable stock.
        Assert.Equal(6m, outcome.Before.QuantityOnHand);
        Assert.Equal(0m, outcome.Quarantined.RestockedQuantity);
        Assert.Equal(0m, outcome.Destroyed.RestockedQuantity);
        Assert.All(
            outcome.Quarantined.Lines.Concat(outcome.Destroyed.Lines),
            line => Assert.False(line.WasRestocked));

        Assert.DoesNotContain(
            outcome.Movements,
            movement => movement.MovementType == StockMovementType.SalesReturn);

        // Four strips were charged at 20.00, and two came back.
        Assert.Equal(40.00m, outcome.Quarantined.RefundTotal + outcome.Destroyed.RefundTotal);
        Assert.Equal(PaymentMethod.Upi, outcome.Quarantined.RefundMethod);
        Assert.Equal(2m, outcome.Sale!.UnitsReturned);

        // Nothing was added to the shelf, so the cached balance and the ledger still agree.
        Assert.True(outcome.Reconciliation.IsReconciled);
    }

    /// <summary>
    /// Nothing can come back that did not go out: not more than the line sold, and not out of a batch the
    /// line never touched.
    /// </summary>
    [Fact]
    public async Task A_return_of_more_than_the_line_sold_is_refused_and_leaves_nothing_behind()
    {
        var sold = $"B-{Unique()}";
        var neverSold = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Over-return test {Unique()}");
            var soldBatchId = await ReceiveAsync(services, medicineId, (sold, Today.AddYears(1), 10m, 4.00m, 20.00m));
            var otherBatchId = await ReceiveAsync(services, medicineId, (neverSold, Today.AddYears(2), 10m, 4.00m, 20.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();
            var returns = services.GetRequiredService<ISaleReturnCatalog>();

            var checkout = await sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 2m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 40.00m }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            var saleId = checkout.Value!.Id;
            var countsBefore = await CountsAsync(services);

            var tooMany = await returns.RecordAsync(new SalesReturnDraft
            {
                SaleId = saleId,
                ReturnedBy = Operator,
                Reason = "Brought back more than were sold",
                Lines =
                [
                    new SalesReturnLineDraft
                    {
                        LineNumber = 1,
                        MedicineBatchId = soldBatchId,
                        Quantity = 3m,
                        Disposition = ReturnDisposition.Restock,
                    },
                ],
            });

            var wrongBatch = await returns.RecordAsync(new SalesReturnDraft
            {
                SaleId = saleId,
                ReturnedBy = Operator,
                Reason = "Wrong pack",
                Lines =
                [
                    new SalesReturnLineDraft
                    {
                        LineNumber = 1,
                        MedicineBatchId = otherBatchId,
                        Quantity = 1m,
                        Disposition = ReturnDisposition.Restock,
                    },
                ],
            });

            var countsAfter = await CountsAsync(services);

            return new
            {
                Sale = await sales.FindAsync(saleId),
                TooMany = tooMany,
                WrongBatch = wrongBatch,
                CountsBefore = countsBefore,
                CountsAfter = countsAfter,
                Batch = await BatchAsync(services, sold),
                OtherBatch = await BatchAsync(services, neverSold),
            };
        });

        Assert.False(outcome.TooMany.Succeeded);
        Assert.Contains("at most 2", outcome.TooMany.Message, StringComparison.Ordinal);

        Assert.False(outcome.WrongBatch.Succeeded);
        Assert.Contains("did not sell batch", outcome.WrongBatch.Message, StringComparison.Ordinal);

        // A refused return is not a partly applied one: no return rows, no movements, no refund, and an
        // invoice and shelf exactly as they were.
        Assert.Equal(outcome.CountsBefore, outcome.CountsAfter);
        Assert.Equal(0m, outcome.Sale!.ReturnedTotal);
        Assert.Equal(0m, outcome.Sale.Lines[0].ReturnedQuantity);
        Assert.False(outcome.Sale.IsFullyReturned);
        Assert.Equal(8m, outcome.Batch.QuantityOnHand);
        Assert.Equal(10m, outcome.OtherBatch.QuantityOnHand);
    }

    /// <summary>A refund cannot be back-dated into a day on which the medicine had not been sold yet.</summary>
    [Fact]
    public async Task A_return_cannot_be_dated_before_the_invoice_it_credits()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Return date test {Unique()}");
            var batchId = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 5m, 4.00m, 20.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();
            var returns = services.GetRequiredService<ISaleReturnCatalog>();

            var checkout = await sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 1m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 20.00m }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            var countsBefore = await CountsAsync(services);

            var refused = await returns.RecordAsync(new SalesReturnDraft
            {
                SaleId = checkout.Value!.Id,
                ReturnedBy = Operator,
                Reason = "Yesterday's return",
                BusinessDate = Today.AddDays(-1),
                Lines =
                [
                    new SalesReturnLineDraft
                    {
                        LineNumber = 1,
                        MedicineBatchId = batchId,
                        Quantity = 1m,
                        Disposition = ReturnDisposition.Restock,
                    },
                ],
            });

            return (
                Refused: refused,
                CountsBefore: countsBefore,
                CountsAfter: await CountsAsync(services),
                Batch: await BatchAsync(services, batch));
        });

        Assert.False(outcome.Refused.Succeeded);
        Assert.Contains("before the invoice's business date", outcome.Refused.Message, StringComparison.Ordinal);
        Assert.Equal(outcome.CountsBefore, outcome.CountsAfter);
        Assert.Equal(4m, outcome.Batch.QuantityOnHand);
    }

    /// <summary>
    /// Two operators take the same last pack back at the same moment. Only one refund may be issued, and
    /// the shelf must not gain a unit twice. The rowversion on the invoice line and its batch allocation
    /// is what decides it, and the losing attempt is retried and then refused on the facts.
    /// </summary>
    [Fact]
    public async Task Two_returns_cannot_each_refund_the_same_last_unit()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Concurrent return test {Unique()}");
            var batchId = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 1m, 4.00m, 20.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();
            var returns = services.GetRequiredService<ISaleReturnCatalog>();

            var checkout = await sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 1m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 20.00m }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            var saleId = checkout.Value!.Id;

            SalesReturnDraft DraftFor(string operatorName) => new()
            {
                SaleId = saleId,
                ReturnedBy = operatorName,
                Reason = "Brought straight back",
                Lines =
                [
                    new SalesReturnLineDraft
                    {
                        LineNumber = 1,
                        MedicineBatchId = batchId,
                        Quantity = 1m,
                        Disposition = ReturnDisposition.Restock,
                    },
                ],
            };

            var attempts = await Task.WhenAll(
                returns.RecordAsync(DraftFor(Operator)),
                returns.RecordAsync(DraftFor("second-operator")));

            return new
            {
                Wins = attempts.Count(attempt => attempt.Succeeded),
                Refusals = attempts
                    .Where(attempt => !attempt.Succeeded)
                    .SelectMany(attempt => attempt.Errors)
                    .ToList(),
                Sale = await sales.FindAsync(saleId),
                Batch = await BatchAsync(services, batch),
                Movements = await MovementsAsync(services, medicineId),
                Reconciliation = await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync(),
            };
        });

        Assert.Equal(1, outcome.Wins);
        Assert.NotEmpty(outcome.Refusals);

        // The one refund is the whole line, and the shelf holds the one pack again - not two.
        Assert.Equal(20.00m, outcome.Sale!.ReturnedTotal);
        Assert.Equal(1m, outcome.Sale.UnitsReturned);
        Assert.Equal(1m, outcome.Batch.QuantityOnHand);
        Assert.Single(
            outcome.Movements,
            movement => movement.MovementType == StockMovementType.SalesReturn);
        Assert.Equal(1m, outcome.Movements.Sum(movement => movement.QuantityDelta));
        Assert.True(outcome.Reconciliation.IsReconciled);
    }

    /// <summary>
    /// Cancelling is a status change on a document that keeps its numbers, and it is only allowed once
    /// everything on the invoice has been returned: the stock a cancellation undoes is put back by the
    /// return, never by erasing the sale.
    /// </summary>
    [Fact]
    public async Task An_invoice_can_only_be_cancelled_once_everything_on_it_has_come_back()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Cancel test {Unique()}");
            var batchId = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 4.00m, 20.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();
            var returns = services.GetRequiredService<ISaleReturnCatalog>();

            var checkout = await sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 2m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 40.00m }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            var saleId = checkout.Value!.Id;

            var tooEarly = await returns.CancelSaleAsync(saleId, Operator, "Rang up in error");

            Assert.True((await returns.RecordAsync(new SalesReturnDraft
            {
                SaleId = saleId,
                ReturnedBy = Operator,
                Reason = "Rang up in error",
                Lines =
                [
                    new SalesReturnLineDraft
                    {
                        LineNumber = 1,
                        MedicineBatchId = batchId,
                        Quantity = 2m,
                        Disposition = ReturnDisposition.Restock,
                    },
                ],
            })).Succeeded);

            var cancelled = await returns.CancelSaleAsync(saleId, Operator, "Rang up in error");
            var twice = await returns.CancelSaleAsync(saleId, Operator, "Rang up in error again");

            var afterCancellation = await returns.RecordAsync(new SalesReturnDraft
            {
                SaleId = saleId,
                ReturnedBy = Operator,
                Reason = "One more strip",
                Lines =
                [
                    new SalesReturnLineDraft
                    {
                        LineNumber = 1,
                        MedicineBatchId = batchId,
                        Quantity = 1m,
                        Disposition = ReturnDisposition.Restock,
                    },
                ],
            });

            return new
            {
                TooEarly = tooEarly,
                Cancelled = cancelled,
                Twice = twice,
                AfterCancellation = afterCancellation,
                Sale = await sales.FindAsync(saleId),
                Batch = await BatchAsync(services, batch),
            };
        });

        Assert.False(outcome.TooEarly.Succeeded);
        Assert.Contains("unreturned", outcome.TooEarly.Message, StringComparison.Ordinal);

        Assert.True(outcome.Cancelled.Succeeded, outcome.Cancelled.Message);

        // The invoice keeps its number, its lines, its payment and its refund; only its status changes.
        var sale = outcome.Sale!;
        Assert.Equal(SaleStatus.Cancelled, sale.Status);
        Assert.Equal(outcome.Cancelled.Value!.InvoiceNumber, sale.InvoiceNumber);
        Assert.Equal(Operator, sale.CancelledBy);
        Assert.Equal("Rang up in error", sale.CancellationReason);
        Assert.Equal(2m, sale.UnitsReturned);
        Assert.Equal(40.00m, sale.ReturnedTotal);
        Assert.True(sale.IsFullyReturned);

        Assert.False(outcome.Twice.Succeeded);
        Assert.Contains("already been cancelled", outcome.Twice.Message, StringComparison.Ordinal);
        Assert.False(outcome.AfterCancellation.Succeeded);
        Assert.Contains("was cancelled", outcome.AfterCancellation.Message, StringComparison.Ordinal);

        // Both units are back on the shelf, and the cancelled sale is still on the books.
        Assert.Equal(10m, outcome.Batch.QuantityOnHand);
        Assert.Equal(2m, sale.Lines[0].ReturnedQuantity);
    }

    /// <summary>
    /// A return is a document in its own right: it is numbered from its own series, it can be looked up
    /// years later by its number, by the invoice the customer brought in, or by the medicine on it, and
    /// each return stays attached to the invoice it credits.
    /// </summary>
    [Fact]
    public async Task Returns_are_found_by_their_number_the_invoice_or_the_medicine()
    {
        var batch = $"B-{Unique()}";
        var medicineName = $"Return search test {Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, medicineName);
            var batchId = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 6m, 4.00m, 20.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();
            var returns = services.GetRequiredService<ISaleReturnCatalog>();

            var checkout = await sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                CustomerName = "Search customer",
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 3m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 60.00m }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            var sale = checkout.Value!;

            var restock = await returns.RecordAsync(new SalesReturnDraft
            {
                SaleId = sale.Id,
                ReturnedBy = Operator,
                Reason = "Unopened",
                RefundMethod = PaymentMethod.Card,
                Lines =
                [
                    new SalesReturnLineDraft
                    {
                        LineNumber = 1,
                        MedicineBatchId = batchId,
                        Quantity = 1m,
                        Disposition = ReturnDisposition.Restock,
                    },
                ],
            });

            var quarantine = await returns.RecordAsync(new SalesReturnDraft
            {
                SaleId = sale.Id,
                ReturnedBy = $"{Operator}-relief",
                Reason = "Recalled by the supplier",
                Lines =
                [
                    new SalesReturnLineDraft
                    {
                        LineNumber = 1,
                        MedicineBatchId = batchId,
                        Quantity = 2m,
                        Disposition = ReturnDisposition.Quarantine,
                    },
                ],
            });

            Assert.True(restock.Succeeded, restock.Message);
            Assert.True(quarantine.Succeeded, quarantine.Message);

            return new
            {
                Sale = sale,
                Reloaded = (await sales.FindAsync(sale.Id))!,
                Restock = restock.Value!,
                Quarantine = quarantine.Value!,
                ByNumber = await returns.FindByReturnNumberAsync(restock.Value!.ReturnNumber),
                Missing = await returns.FindByReturnNumberAsync($"RET-{Unique()}"),
                BySale = await returns.SearchAsync(new SalesReturnQuery { SaleId = sale.Id }),
                ByNumberTerm = await returns.SearchAsync(
                    new SalesReturnQuery { Term = quarantine.Value!.ReturnNumber }),
                ByInvoiceTerm = await returns.SearchAsync(new SalesReturnQuery { Term = sale.InvoiceNumber }),
                ByMedicineTerm = await returns.SearchAsync(new SalesReturnQuery { Term = medicineName }),
                Restocked = await returns.SearchAsync(
                    new SalesReturnQuery { SaleId = sale.Id, Disposition = ReturnDisposition.Restock }),
                FromTomorrow = await returns.SearchAsync(
                    new SalesReturnQuery { FromBusinessDate = Today.AddDays(1) }),
                ToYesterday = await returns.SearchAsync(
                    new SalesReturnQuery { ToBusinessDate = Today.AddDays(-1) }),
            };
        });

        // Two returns on the day, taken from the same year's series, never reusing a number.
        Assert.NotEqual(outcome.Restock.ReturnNumber, outcome.Quarantine.ReturnNumber);
        Assert.StartsWith($"RET-{Today.Year}-", outcome.Restock.ReturnNumber, StringComparison.Ordinal);
        Assert.StartsWith($"RET-{Today.Year}-", outcome.Quarantine.ReturnNumber, StringComparison.Ordinal);

        Assert.NotNull(outcome.ByNumber);
        Assert.Equal(outcome.Restock.Id, outcome.ByNumber!.Id);
        Assert.Equal(outcome.Restock.RefundTotal, outcome.ByNumber.RefundTotal);
        Assert.Null(outcome.Missing);

        Assert.Equal(2, outcome.BySale.Count);
        Assert.Equal(outcome.Sale.InvoiceNumber, outcome.BySale[0].InvoiceNumber);
        Assert.Equal("Search customer", outcome.BySale[0].CustomerName);
        Assert.Equal(3m, outcome.BySale.Sum(item => item.ReturnedQuantity));
        Assert.Equal(60.00m, outcome.BySale.Sum(item => item.RefundTotal));
        Assert.Equal(1m, outcome.BySale.Sum(item => item.RestockedQuantity));

        // The invoice's own returns are on it, in the same shape as the history list.
        Assert.Equal(2, outcome.Reloaded.Returns.Count);
        Assert.Equal(1m, outcome.Reloaded.Returns.Sum(item => item.RestockedQuantity));
        Assert.Equal(60.00m, outcome.Reloaded.Returns.Sum(item => item.RefundTotal));

        var byNumber = Assert.Single(outcome.ByNumberTerm);
        Assert.Equal(outcome.Quarantine.Id, byNumber.Id);

        Assert.Contains(outcome.ByInvoiceTerm, item => item.Id == outcome.Restock.Id);
        Assert.Contains(outcome.ByMedicineTerm, item => item.Id == outcome.Quarantine.Id);

        var restocked = Assert.Single(outcome.Restocked);
        Assert.Equal(outcome.Restock.Id, restocked.Id);
        Assert.Equal(ReturnDisposition.Restock, restocked.Lines[0].Disposition);

        // Date windows are honoured: nothing outside them comes back, and neither of today's returns is
        // dated far enough out to fall into one. Other tests share this database, so the check is stated
        // as an invariant over the rows rather than a count.
        Assert.All(
            outcome.FromTomorrow,
            item => Assert.True(
                item.BusinessDate >= Today.AddDays(1),
                $"{item.ReturnNumber} is dated {item.BusinessDate}, before tomorrow."));
        Assert.All(
            outcome.ToYesterday,
            item => Assert.True(
                item.BusinessDate <= Today.AddDays(-1),
                $"{item.ReturnNumber} is dated {item.BusinessDate}, after yesterday."));
        Assert.DoesNotContain(outcome.FromTomorrow, item => item.Id == outcome.Restock.Id);
        Assert.DoesNotContain(outcome.FromTomorrow, item => item.Id == outcome.Quarantine.Id);
        Assert.DoesNotContain(outcome.ToYesterday, item => item.Id == outcome.Restock.Id);
        Assert.DoesNotContain(outcome.ToYesterday, item => item.Id == outcome.Quarantine.Id);
    }

    private async Task<int> SeedMedicineAsync(
        IServiceProvider services,
        string displayName,
        decimal taxRatePercent = 0m)
    {
        var medicine = await services.GetRequiredService<IMedicineCatalog>().CreateAsync(new MedicineDraft
        {
            DisplayName = displayName,
            DosageForm = DosageForm.Tablet,
            Strength = "500 mg",
            PackDescription = "Strip of 10 tablets",
            TaxRatePercent = taxRatePercent,
        });

        Assert.True(medicine.Succeeded, medicine.Message);
        return medicine.Value!.Id;
    }

    /// <summary>
    /// Puts stock on the shelf through the real purchasing workflow, so the batches, their receipt
    /// movements and the purchase behind them are exactly what a pharmacy would have.
    /// </summary>
    private async Task<int> ReceiveAsync(
        IServiceProvider services,
        int medicineId,
        (string BatchNumber, DateOnly ExpiryDate, decimal Quantity, decimal UnitCost, decimal SellingPrice) line)
    {
        var supplier = await services.GetRequiredService<ISupplierCatalog>().CreateAsync(
            new SupplierDraft { Name = $"Return test supplier {Unique()}" });
        Assert.True(supplier.Succeeded, supplier.Message);

        var catalog = services.GetRequiredService<IPurchaseCatalog>();

        var created = await catalog.CreateAsync(new PurchaseDraft
        {
            SupplierId = supplier.Value!.Id,
            InvoiceNumber = $"RET-{Unique()}",
            InvoiceDate = Today,
            Lines =
            [
                new PurchaseLineDraft
                {
                    MedicineId = medicineId,
                    BatchNumber = line.BatchNumber,
                    ExpiryDate = line.ExpiryDate,
                    Quantity = line.Quantity,
                    UnitCost = line.UnitCost,
                    SellingPrice = line.SellingPrice,
                },
            ],
        });

        Assert.True(created.Succeeded, created.Message);

        var received = await catalog.ReceiveAsync(created.Value!.Id, Today, Operator);
        Assert.True(received.Succeeded, received.Message);

        var purchase = await catalog.FindAsync(created.Value.Id);
        var receivedLine = Assert.Single(purchase!.Lines);
        Assert.NotNull(receivedLine.MedicineBatchId);
        return receivedLine.MedicineBatchId!.Value;
    }

    /// <summary>A batch with stock still on the shelf, so the assertion also proves it is sellable.</summary>
    private static async Task<BatchStockDto> BatchAsync(IServiceProvider services, string batchNumber)
    {
        var batches = await services.GetRequiredService<IStockQueries>().ListBatchesAsync(
            new BatchStockQuery { Term = batchNumber, InStockOnly = false });

        return Assert.Single(batches);
    }

    private static Task<IReadOnlyList<StockMovementDto>> MovementsAsync(IServiceProvider services, int medicineId)
        => services.GetRequiredService<IStockQueries>()
            .ListMovementsAsync(new StockMovementQuery { MedicineId = medicineId });

    /// <summary>Counts the rows a return would have had to write, to prove a refused one wrote none.</summary>
    private static async Task<(int Returns, int Lines, int Movements)> CountsAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<MediStockDbContext>();

        return (
            await db.SalesReturns.CountAsync(),
            await db.SalesReturnLines.CountAsync(),
            await db.StockMovements.CountAsync());
    }

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];
}
