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
/// The point of sale against a real SQL Server database. A checkout is the one operation that takes
/// money and moves stock at the same time, so these tests are mostly about what must NOT happen: a
/// basket must never be charged twice for the same unit, never be charged at all when the stock is
/// short, and never leave an invoice behind that a failed transaction did not finish.
/// </summary>
[Collection(TestDatabaseCollection.Name)]
public class SaleCatalogTests
{
    private const string Operator = "integration-test";

    private static DateOnly Today => BusinessTime.Today;

    private readonly TestDatabaseFixture _fixture;

    public SaleCatalogTests(TestDatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// The whole write in one go: the invoice, its lines, its batch allocations, its payment, the
    /// reduced shelf, and one sale movement per batch taken. First-expiry-first-out decides which batch
    /// goes first, and the invoice snapshots the batch it took so a reprint years later still says
    /// exactly what left the shelf.
    /// </summary>
    [Fact]
    public async Task A_checkout_takes_the_earliest_expiring_batches_first_and_records_everything()
    {
        var early = $"B-{Unique()}";
        var late = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Sale test {Unique()}", taxRatePercent: 12m);
            await ReceiveAsync(services, medicineId, (early, Today.AddDays(30), 10m, 12.50m, 20.00m));
            await ReceiveAsync(services, medicineId, (late, Today.AddDays(400), 10m, 15.00m, 25.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();

            var draft = new SaleDraft
            {
                SoldBy = Operator,
                CustomerName = "Test customer",
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 15m }],
            };

            var quote = await sales.QuoteAsync(draft);
            var checkout = await sales.CheckoutAsync(draft with
            {
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = quote.Total }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            return new
            {
                Quote = quote,
                Sale = await sales.FindAsync(checkout.Value!.Id),
                Early = await BatchAsync(services, early),
                Late = await BatchAsync(services, late),
                Movements = await MovementsAsync(services, medicineId),
                Reconciliation = await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync(),
            };
        });

        // 10 units out of the batch expiring in a month, the remaining 5 out of the one expiring next year.
        var sale = outcome.Sale!;
        var line = Assert.Single(sale.Lines);
        Assert.Equal(15m, line.Quantity);
        Assert.Equal(20.00m, line.UnitPrice);
        Assert.Equal(300.00m, line.GrossAmount);
        Assert.Equal(36.00m, line.TaxAmount);
        Assert.Equal(336.00m, line.LineTotal);

        var allocations = line.Allocations.OrderBy(a => a.ExpiryDate).ToList();
        Assert.Equal(2, allocations.Count);
        Assert.Equal(early, allocations[0].BatchNumber);
        Assert.Equal(10m, allocations[0].Quantity);
        Assert.Equal(late, allocations[1].BatchNumber);
        Assert.Equal(5m, allocations[1].Quantity);

        // Weighted across both batches: (10 x 12.50 + 5 x 15.00) / 15.
        Assert.Equal(13.33m, line.UnitCost);

        Assert.Equal(336.00m, sale.Total);
        Assert.Equal(PaymentMethod.Cash, Assert.Single(sale.Payments).Method);
        Assert.Equal(336.00m, sale.Payments[0].Amount);
        Assert.Equal(SaleStatus.Completed, sale.Status);
        Assert.StartsWith("INV-", sale.InvoiceNumber, StringComparison.Ordinal);

        // The shelf, not just the invoice.
        Assert.Equal(0m, outcome.Early.QuantityOnHand);
        Assert.Equal(BatchStatus.Depleted, outcome.Early.Status);
        Assert.Equal(5m, outcome.Late.QuantityOnHand);

        var movements = outcome.Movements.Where(m => m.MovementType == StockMovementType.Sale).ToList();
        Assert.Equal(2, movements.Count);
        Assert.Equal(-10m, movements.Single(m => m.MedicineBatchId == outcome.Early.BatchId).QuantityDelta);
        Assert.Equal(-5m, movements.Single(m => m.MedicineBatchId == outcome.Late.BatchId).QuantityDelta);
        Assert.All(movements, m => Assert.Equal($"SALE-{sale.Id}", m.Reference));
        Assert.All(movements, m => Assert.Equal(Today, m.BusinessDate));
        Assert.True(outcome.Reconciliation.IsReconciled);
    }

    /// <summary>
    /// What the operator was shown is what the customer is charged. The quote and the checkout price the
    /// basket through the same code, so if they ever disagree one of them has to be wrong.
    /// </summary>
    [Fact]
    public async Task A_quote_and_its_checkout_agree_to_the_paisa()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Quote test {Unique()}", taxRatePercent: 5m);
            await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 100m, 4.00m, 7.50m));

            var sales = services.GetRequiredService<ISaleCatalog>();

            var draft = new SaleDraft
            {
                SoldBy = Operator,
                Lines =
                [
                    new SaleLineDraft { MedicineId = medicineId, Quantity = 3m, DiscountAmount = 1.50m },
                    new SaleLineDraft { MedicineId = medicineId, Quantity = 2m },
                ],
            };

            var quote = await sales.QuoteAsync(draft);
            Assert.True(quote.IsCheckoutReady, string.Join(" ", quote.Blockers));

            var checkout = await sales.CheckoutAsync(draft with
            {
                Payments = [new PaymentDraft { Method = PaymentMethod.Upi, Amount = quote.Total, Reference = "UPI-TEST" }],
            });

            return (
                Quote: quote,
                Checkout: checkout,
                Sale: checkout.Succeeded ? await sales.FindAsync(checkout.Value!.Id) : null);
        });

        Assert.True(outcome.Checkout.Succeeded, outcome.Checkout.Message);

        var sale = outcome.Sale!;
        Assert.Equal(outcome.Quote.SubTotal, sale.SubTotal);
        Assert.Equal(outcome.Quote.DiscountTotal, sale.DiscountTotal);
        Assert.Equal(outcome.Quote.TaxableTotal, sale.TaxableTotal);
        Assert.Equal(outcome.Quote.TaxTotal, sale.TaxTotal);
        Assert.Equal(outcome.Quote.Total, sale.Total);
        Assert.Equal(outcome.Quote.CostTotal, sale.CostTotal);

        // 5 units at 7.50 less the 1.50 discount, plus 5 percent tax on the discounted amount.
        Assert.Equal(37.50m, sale.SubTotal);
        Assert.Equal(1.50m, sale.DiscountTotal);
        Assert.Equal(36.00m, sale.TaxableTotal);
        Assert.Equal(1.80m, sale.TaxTotal);
        Assert.Equal(37.80m, sale.Total);

        // Line numbers are consecutive from one, so a printed invoice reads down the page.
        Assert.Equal(new[] { 1, 2 }, sale.Lines.Select(line => line.LineNumber).ToList());
    }

    [Fact]
    public async Task An_expired_batch_is_skipped_even_when_it_expires_soonest()
    {
        var expired = $"B-{Unique()}";
        var sellable = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Expiry test {Unique()}");
            await SeedExpiredBatchAsync(services, medicineId, expired, 25m);
            await ReceiveAsync(services, medicineId, (sellable, Today.AddYears(1), 5m, 3.00m, 6.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();
            var checkout = await sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 5m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 30.00m }],
            });

            return (
                Checkout: checkout,
                Sale: checkout.Succeeded ? await sales.FindAsync(checkout.Value!.Id) : null,
                Expired: await BatchAsync(services, expired),
                Sellable: await BatchAsync(services, sellable));
        });

        Assert.True(outcome.Checkout.Succeeded, outcome.Checkout.Message);
        Assert.Equal(sellable, Assert.Single(outcome.Sale!.Lines).Allocations[0].BatchNumber);

        // The expired batch is untouched, still holding every unit it was received with.
        Assert.Equal(25m, outcome.Expired.QuantityOnHand);
        Assert.Equal(0m, outcome.Sellable.QuantityOnHand);
    }

    /// <summary>
    /// A basket is priced before it is charged, so the operator learns about the shortfall with the
    /// customer still at the until. Nothing at all is written: no invoice, no payment, no movement and
    /// no reduction of the part of the basket that was available.
    /// </summary>
    [Fact]
    public async Task A_basket_that_is_short_of_stock_is_refused_and_writes_nothing()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Short stock test {Unique()}");
            await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 4m, 2.00m, 5.00m));

            var before = await CountsAsync(services);

            var checkout = await services.GetRequiredService<ISaleCatalog>().CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines =
                [
                    new SaleLineDraft { MedicineId = medicineId, Quantity = 3m },
                    new SaleLineDraft { MedicineId = medicineId, Quantity = 9m },
                ],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 60.00m }],
            });

            return (
                Checkout: checkout,
                Before: before,
                After: await CountsAsync(services),
                Batch: await BatchAsync(services, batch));
        });

        Assert.False(outcome.Checkout.Succeeded);
        Assert.Contains("can be dispensed today", outcome.Checkout.Message, StringComparison.Ordinal);

        Assert.Equal(outcome.Before.Sales, outcome.After.Sales);
        Assert.Equal(outcome.Before.Payments, outcome.After.Payments);
        Assert.Equal(outcome.Before.Movements, outcome.After.Movements);

        // Not even the three units that were available left the shelf.
        Assert.Equal(4m, outcome.Batch.QuantityOnHand);
    }

    [Fact]
    public async Task A_settlement_that_does_not_match_the_invoice_total_is_refused()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Settlement test {Unique()}");
            await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 4.00m, 10.00m));

            var before = await CountsAsync(services);

            var checkout = await services.GetRequiredService<ISaleCatalog>().CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 2m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 5.00m }],
            });

            return (
                Checkout: checkout,
                Before: before,
                After: await CountsAsync(services),
                Batch: await BatchAsync(services, batch));
        });

        Assert.False(outcome.Checkout.Succeeded);
        Assert.NotEmpty(outcome.Checkout.Message);
        Assert.Equal(outcome.Before.Sales, outcome.After.Sales);
        Assert.Equal(outcome.Before.Movements, outcome.After.Movements);
        Assert.Equal(10m, outcome.Batch.QuantityOnHand);
    }

    /// <summary>
    /// The race the whole design exists for. Two tills sell the last units at the same moment, each
    /// having seen them available. The batch carries a rowversion, so the second quantity write matches
    /// no row and its transaction rolls back: one customer is served, the other is asked to choose again.
    /// </summary>
    [Fact]
    public async Task Two_tills_racing_for_the_last_units_cannot_both_sell_them()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Sale race test {Unique()}");
            await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 4.00m, 10.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();

            SaleDraft DraftFor(decimal quantity) => new()
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = quantity }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = quantity * 10.00m }],
            };

            var attempts = await Task.WhenAll(
                sales.CheckoutAsync(DraftFor(7m)),
                sales.CheckoutAsync(DraftFor(7m)));

            return (
                Wins: attempts.Count(attempt => attempt.Succeeded),
                Refusals: attempts.Where(attempt => !attempt.Succeeded).Select(attempt => attempt.Message).ToList(),
                Batch: await BatchAsync(services, batch),
                Movements: await MovementsAsync(services, medicineId),
                Sales: await CountsAsync(services),
                Reconciliation: await services.GetRequiredService<IStockQueries>().CheckReconciliationAsync());
        });

        Assert.Equal(1, outcome.Wins);
        Assert.All(outcome.Refusals, message => Assert.False(string.IsNullOrWhiteSpace(message)));

        // Seven units are gone and three remain - never minus four.
        Assert.Equal(3m, outcome.Batch.QuantityOnHand);

        var sold = outcome.Movements.Where(m => m.MovementType == StockMovementType.Sale).ToList();
        Assert.Equal(-7m, Assert.Single(sold).QuantityDelta);

        // The loser's invoice number was released with its transaction, so the ledger holds one sale.
        Assert.True(outcome.Reconciliation.IsReconciled);
    }

    /// <summary>
    /// One batch of stock, several tills, no duplicate invoice numbers. The number comes from a
    /// single atomic statement inside each checkout's transaction, and a unique index refuses any
    /// duplicate that a race could still produce.
    /// </summary>
    [Fact]
    public async Task Concurrent_checkouts_never_issue_the_same_invoice_number_twice()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Invoice number test {Unique()}");
            await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 20m, 1.00m, 2.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();

            var attempts = await Task.WhenAll(Enumerable.Range(0, 6).Select(_ => sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 1m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 2.00m }],
            })));

            var invoices = new List<string>();
            foreach (var attempt in attempts.Where(a => a.Succeeded))
            {
                invoices.Add((await sales.FindAsync(attempt.Value!.Id))!.InvoiceNumber);
            }

            return (
                Wins: attempts.Count(attempt => attempt.Succeeded),
                Refusals: attempts.Where(attempt => !attempt.Succeeded).Select(attempt => attempt.Message).ToList(),
                Invoices: invoices,
                Batch: await BatchAsync(services, batch));
        });

        Assert.Empty(outcome.Refusals);
        Assert.Equal(6, outcome.Wins);
        Assert.Equal(6, outcome.Invoices.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(6, outcome.Invoices.Count);

        // Every attempt took exactly one unit, so fourteen are left and never thirteen.
        Assert.Equal(14m, outcome.Batch.QuantityOnHand);
    }

    [Fact]
    public async Task An_inactive_medicine_cannot_be_sold()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Inactive sale test {Unique()}");
            await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 4.00m, 10.00m));

            var catalogue = services.GetRequiredService<IMedicineCatalog>();
            Assert.True((await catalogue.SetActiveAsync(medicineId, isActive: false)).Succeeded);

            return (
                Checkout: await services.GetRequiredService<ISaleCatalog>().CheckoutAsync(new SaleDraft
                {
                    SoldBy = Operator,
                    Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 1m }],
                    Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 10.00m }],
                }),
                Batch: await BatchAsync(services, batch));
        });

        Assert.False(outcome.Checkout.Succeeded);
        Assert.Contains("no longer active", outcome.Checkout.Message, StringComparison.Ordinal);
        Assert.Equal(10m, outcome.Batch.QuantityOnHand);
    }

    /// <summary>
    /// A customer who insists on a particular batch is served from that batch first, and only then from
    /// the earliest-expiring stock - an explicit request outranks the default policy, not the policy
    /// itself.
    /// </summary>
    [Fact]
    public async Task A_preferred_batch_is_used_before_the_earliest_expiring_one()
    {
        var early = $"B-{Unique()}";
        var late = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Preference test {Unique()}");
            await ReceiveAsync(services, medicineId, (early, Today.AddDays(30), 10m, 4.00m, 10.00m));
            var lateBatchId = await ReceiveAsync(services, medicineId, (late, Today.AddDays(400), 10m, 4.00m, 10.00m));

            var checkout = await services.GetRequiredService<ISaleCatalog>().CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 12m, PreferredBatchId = lateBatchId }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 120.00m }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            return (
                Sale: await services.GetRequiredService<ISaleCatalog>().FindAsync(checkout.Value!.Id),
                Early: await BatchAsync(services, early),
                Late: await BatchAsync(services, late));
        });

        var allocations = outcome.Sale!.Lines[0].Allocations;

        // Ten units came out of the batch the customer asked for and only the two that were still
        // missing came out of the earlier-expiring stock.
        Assert.Equal(10m, allocations.Single(a => a.BatchNumber == late).Quantity);
        Assert.Equal(2m, allocations.Single(a => a.BatchNumber == early).Quantity);

        // On the invoice itself the allocations read in expiry order, whichever batch was picked first.
        Assert.Equal(
            new[] { early, late },
            allocations.OrderBy(a => a.ExpiryDate).ThenBy(a => a.MedicineBatchId).Select(a => a.BatchNumber).ToList());

        Assert.Equal(8m, outcome.Early.QuantityOnHand);
        Assert.Equal(0m, outcome.Late.QuantityOnHand);
    }

    [Fact]
    public async Task Sales_can_be_found_by_invoice_number_and_by_date_range()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"History test {Unique()}");
            await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 4.00m, 10.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();
            var checkout = await sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = "history-operator",
                CustomerName = "History customer",
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 2m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Card, Amount = 20.00m, Reference = "CARD-1" }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            var stored = await sales.FindAsync(checkout.Value!.Id);

            return (
                Stored: stored,
                ByNumber: await sales.FindByInvoiceNumberAsync(stored!.InvoiceNumber),
                ByRange: await sales.SearchAsync(new SaleQuery
                {
                    FromBusinessDate = Today,
                    ToBusinessDate = Today,
                    Term = "History customer",
                }),
                ByMethod: await sales.SearchAsync(new SaleQuery { PaymentMethod = PaymentMethod.Card, Take = 200 }),
                OutsideRange: await sales.SearchAsync(new SaleQuery
                {
                    FromBusinessDate = Today.AddDays(30),
                    ToBusinessDate = Today.AddDays(31),
                    Term = stored.InvoiceNumber,
                })
            );
        });

        Assert.Equal(outcome.Stored!.Id, outcome.ByNumber!.Id);
        Assert.Equal("History customer", outcome.ByNumber.CustomerName);
        Assert.Contains(outcome.ByRange, sale => sale.Id == outcome.Stored.Id);
        Assert.Contains(outcome.ByMethod, sale => sale.Id == outcome.Stored.Id);

        // A date range that cannot contain the sale must not return it, even by exact invoice number.
        Assert.DoesNotContain(outcome.OutsideRange, sale => sale.Id == outcome.Stored.Id);
    }

    /// <summary>
    /// The dashboard reads the same invoices the till wrote, so the day's takings, the units sold and
    /// the tax all have to move together when one more sale is rung up.
    /// </summary>
    [Fact]
    public async Task Daily_totals_move_by_exactly_the_invoice_that_was_rung_up()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Daily total test {Unique()}", taxRatePercent: 12m);
            await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 20m, 4.00m, 10.00m));

            var queries = services.GetRequiredService<ISalesQueries>();
            var before = await queries.GetDailySummaryAsync(Today);
            var methodsBefore = await queries.GetPaymentMethodTotalsAsync(Today, Today);

            var checkout = await services.GetRequiredService<ISaleCatalog>().CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 3m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 33.60m }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            return (
                Before: before,
                MethodsBefore: methodsBefore,
                After: await queries.GetDailySummaryAsync(Today),
                Range: await queries.GetDailySummariesAsync(Today, Today),
                Methods: await queries.GetPaymentMethodTotalsAsync(Today, Today),
                Top: await queries.GetTopMedicinesAsync(Today, Today, take: 500),
                MedicineId: medicineId
            );
        });

        Assert.Equal(outcome.Before.SaleCount + 1, outcome.After.SaleCount);
        Assert.Equal(outcome.Before.Total + 33.60m, outcome.After.Total);
        Assert.Equal(outcome.Before.TaxTotal + 3.60m, outcome.After.TaxTotal);
        Assert.Equal(outcome.Before.UnitsSold + 3m, outcome.After.UnitsSold);

        var day = Assert.Single(outcome.Range);
        Assert.Equal(Today, day.BusinessDate);
        Assert.Equal(outcome.After.Total, day.Total);

        var cashBefore = outcome.MethodsBefore.Single(total => total.Method == PaymentMethod.Cash).Amount;
        var cash = Assert.Single(outcome.Methods, total => total.Method == PaymentMethod.Cash);
        Assert.Equal(cashBefore + 33.60m, cash.Amount);

        // The day's takings are every method added up, not whichever one happens to be first.
        Assert.Equal(outcome.After.Total, outcome.Methods.Sum(total => total.Amount));

        var top = Assert.Single(outcome.Top, total => total.MedicineId == outcome.MedicineId);
        Assert.Equal(3m, top.UnitsSold);
        Assert.Equal(33.60m, top.Total);
    }

    [Fact]
    public async Task Every_sale_on_an_invoice_is_still_readable_after_it_has_been_settled()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Receipt test {Unique()}", taxRatePercent: 12m);
            await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 4.00m, 10.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();
            var checkout = await sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                CustomerName = "Receipt customer",
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 2m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 22.40m }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            var stored = await sales.FindAsync(checkout.Value!.Id);
            var exporter = services.GetRequiredService<IReceiptExporter>();

            var first = await exporter.ExportAsync(stored!);
            var second = await exporter.ExportAsync(stored!);

            return (
                Sale: stored,
                First: first,
                Second: second,
                Document: await File.ReadAllTextAsync(first.FilePath),
                Batch: await BatchAsync(services, batch));
        });

        var sale = outcome.Sale!;
        Assert.Equal(sale.InvoiceNumber, outcome.First.InvoiceNumber);
        Assert.Equal(outcome.First.FilePath, outcome.Second.FilePath);
        Assert.True(File.Exists(outcome.First.FilePath));

        Assert.Contains(sale.InvoiceNumber, outcome.Document, StringComparison.Ordinal);
        Assert.Contains("Receipt customer", outcome.Document, StringComparison.Ordinal);
        Assert.Contains(batch, outcome.Document, StringComparison.Ordinal);
        Assert.Contains("22.40", outcome.Document, StringComparison.Ordinal);

        // Reprinting a stored invoice is a file write and nothing else: the shelf has not moved.
        Assert.Equal(8m, outcome.Batch.QuantityOnHand);
    }

    [Fact]
    public async Task An_invoice_is_kept_when_the_same_basket_is_checked_out_twice()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var medicineId = await SeedMedicineAsync(services, $"Repeat sale test {Unique()}");
            await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 4.00m, 10.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();

            var draft = new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 1m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 10.00m }],
            };

            var first = await sales.CheckoutAsync(draft);
            var second = await sales.CheckoutAsync(draft);

            return (
                First: first,
                Second: second,
                FirstSale: first.Succeeded ? await sales.FindAsync(first.Value!.Id) : null,
                SecondSale: second.Succeeded ? await sales.FindAsync(second.Value!.Id) : null,
                Batch: await BatchAsync(services, batch),
                Movements: await MovementsAsync(services, medicineId));
        });

        Assert.True(outcome.First.Succeeded, outcome.First.Message);
        Assert.True(outcome.Second.Succeeded, outcome.Second.Message);

        // Selling the same basket twice is two genuine sales, not a mistake: they are two invoices,
        // two payments and two movements, and the shelf is two units lighter.
        Assert.NotEqual(outcome.FirstSale!.InvoiceNumber, outcome.SecondSale!.InvoiceNumber);
        Assert.NotEqual(outcome.FirstSale.Id, outcome.SecondSale.Id);
        Assert.Equal(8m, outcome.Batch.QuantityOnHand);
        Assert.Equal(2, outcome.Movements.Count(m => m.MovementType == StockMovementType.Sale));
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
            new SupplierDraft { Name = $"Sale test supplier {Unique()}" });
        Assert.True(supplier.Succeeded, supplier.Message);

        var catalog = services.GetRequiredService<IPurchaseCatalog>();

        var created = await catalog.CreateAsync(new PurchaseDraft
        {
            SupplierId = supplier.Value!.Id,
            InvoiceNumber = $"SALE-{Unique()}",
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

    /// <summary>
    /// A batch that was already past its expiry when it arrived, which the receiving workflow rightly
    /// refuses to create. Written straight to the database because it is the state of a shelf that has
    /// been left standing too long, and the till has to cope with it rather than being protected from it.
    /// </summary>
    private static async Task SeedExpiredBatchAsync(
        IServiceProvider services,
        int medicineId,
        string batchNumber,
        decimal quantity)
    {
        var db = services.GetRequiredService<MediStockDbContext>();
        var receivedOn = Today.AddDays(-60);

        var batch = MedicineBatch.Receive(
            medicineId,
            batchNumber,
            Today.AddDays(-1),
            quantity,
            5.00m,
            9.00m,
            receivedOn,
            nowUtc: DateTimeOffset.UtcNow);

        db.MedicineBatches.Add(batch);
        await db.SaveChangesAsync();

        db.StockMovements.Add(StockMovement.Record(
            medicineId,
            batch.Id,
            StockMovementType.Receipt,
            quantity,
            batch.QuantityOnHand,
            receivedOn,
            Operator,
            reference: "TEST-EXPIRED-BATCH"));

        await db.SaveChangesAsync();
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

    private static async Task<(int Sales, int Payments, int Movements)> CountsAsync(IServiceProvider services)
    {
        var db = services.GetRequiredService<MediStockDbContext>();

        return (
            await db.Sales.CountAsync(),
            await db.Payments.CountAsync(),
            await db.StockMovements.CountAsync());
    }

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];
}
