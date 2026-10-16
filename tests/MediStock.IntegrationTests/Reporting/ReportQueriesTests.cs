using System.Globalization;
using MediStock.Application.Catalogue;
using MediStock.Application.Inventory;
using MediStock.Application.Purchasing;
using MediStock.Application.Reporting;
using MediStock.Application.Sales;
using MediStock.Application.Suppliers;
using MediStock.Domain.Catalogue;
using MediStock.Domain.Purchasing;
using MediStock.Domain.Sales;
using Microsoft.Extensions.DependencyInjection;

namespace MediStock.IntegrationTests.Reporting;

/// <summary>
/// The reports against a real SQL Server database. A report is only worth having if its figures can be
/// traced back to the transactions behind them, so every test here adds known transactions and then
/// checks the report moved by exactly the amount those transactions account for — including the
/// business date a refund belongs to, which is the boundary a daily takings figure lives or dies by.
/// </summary>
[Collection(TestDatabaseCollection.Name)]
public class ReportQueriesTests
{
    private const string Operator = "integration-test";

    /// <summary>Daily sales columns, in the order the report declares them.</summary>
    private const int DailyDate = 0;
    private const int DailyInvoices = 1;
    private const int DailyUnitsSold = 2;
    private const int DailyUnitsReturned = 3;
    private const int DailyGross = 4;
    private const int DailyTax = 6;
    private const int DailySales = 7;
    private const int DailyReturns = 8;
    private const int DailyRefunds = 9;
    private const int DailyNet = 10;
    private const int DailyCost = 11;
    private const int DailyMargin = 12;

    private static DateOnly Today => BusinessTime.Today;

    private readonly TestDatabaseFixture _fixture;

    public ReportQueriesTests(TestDatabaseFixture fixture) => _fixture = fixture;

    /// <summary>
    /// Two strips sold for 44.80 and one of them refunded, paid back by UPI rather than in the cash the
    /// customer originally used. The day has to show the refund against the method the money left by, and
    /// the margin has to absorb both halves of the transaction rather than a refund being free.
    /// </summary>
    [Fact]
    public async Task The_day_s_takings_net_the_refunds_and_the_method_they_were_paid_back_by()
    {
        var batch = $"B-{Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var reports = services.GetRequiredService<IReportQueries>();

            var dailyToday = new ReportRequest { Kind = ReportKind.DailySales, From = Today, To = Today };
            var methodsToday = new ReportRequest
            {
                Kind = ReportKind.SalesByPaymentMethod,
                From = Today,
                To = Today,
            };

            var dailyBefore = await reports.RunAsync(dailyToday);
            var methodsBefore = await reports.RunAsync(methodsToday);

            var medicineId = await SeedMedicineAsync(services, $"Report test {Unique()}", taxRatePercent: 12m);
            var batchId = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 10m, 4.00m, 20.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();
            var returns = services.GetRequiredService<ISaleReturnCatalog>();

            var checkout = await sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 2m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 44.80m }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            var refund = await returns.RecordAsync(new SalesReturnDraft
            {
                SaleId = checkout.Value!.Id,
                ReturnedBy = Operator,
                Reason = "Unopened strip",
                RefundMethod = PaymentMethod.Upi,
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

            Assert.True(refund.Succeeded, refund.Message);

            return new
            {
                DailyBefore = dailyBefore,
                DailyAfter = await reports.RunAsync(dailyToday),
                MethodsBefore = methodsBefore,
                MethodsAfter = await reports.RunAsync(methodsToday),
                Refund = refund.Value!,
            };
        });

        // A shared test database holds other tests' sales from today too, so the assertions are about what
        // this test moved the day by, which is what the report has to get exactly right.
        var dayBefore = outcome.DailyBefore.Rows.SingleOrDefault(row => row[DailyDate] is DateOnly date && date == Today);
        var day = Assert.Single(
            outcome.DailyAfter.Rows,
            row => row[DailyDate] is DateOnly date && date == Today);

        Assert.Equal(Count(dayBefore, DailyInvoices) + 1, Count(day, DailyInvoices));
        Assert.Equal(Money(dayBefore, DailyUnitsSold) + 2m, Money(day, DailyUnitsSold));
        Assert.Equal(Money(dayBefore, DailyUnitsReturned) + 1m, Money(day, DailyUnitsReturned));

        // 2 x 20.00 charged, 12% tax on it, and half of each coming back with the returned strip.
        Assert.Equal(Money(dayBefore, DailyGross) + 40.00m, Money(day, DailyGross));
        Assert.Equal(Money(dayBefore, DailySales) + 44.80m, Money(day, DailySales));
        Assert.Equal(Money(dayBefore, DailyRefunds) + 22.40m, Money(day, DailyRefunds));
        Assert.Equal(Money(dayBefore, DailyReturns) + 1, Count(day, DailyReturns));

        // The refund is what the return recorded, not a re-derived figure.
        Assert.Equal(22.40m, outcome.Refund.RefundTotal);
        Assert.Equal(Money(dayBefore, DailyTax) + 2.40m, Money(day, DailyTax));
        Assert.Equal(Money(dayBefore, DailyNet) + 22.40m, Money(day, DailyNet));
        Assert.Equal(Money(dayBefore, DailyCost) + 4.00m, Money(day, DailyCost));
        Assert.Equal(Money(dayBefore, DailyMargin) + 16.00m, Money(day, DailyMargin));

        // The figures are internally consistent: net is sales less refunds, margin is net less tax and cost.
        Assert.Equal(Money(day, DailySales) - Money(day, DailyRefunds), Money(day, DailyNet));
        Assert.Equal(Money(day, DailyNet) - Money(day, DailyTax) - Money(day, DailyCost), Money(day, DailyMargin));

        // The last line is the day's total, and it adds up to the rows above it.
        var totals = outcome.DailyAfter.Rows[^1];
        Assert.Equal("Total", totals[DailyDate]);
        Assert.Equal(
            outcome.DailyAfter.Rows.Take(outcome.DailyAfter.Rows.Count - 1).Sum(row => Money(row, DailyNet)),
            Money(totals, DailyNet));

        var cash = Row(outcome.MethodsAfter.Rows, "Cash");
        var cashBefore = Row(outcome.MethodsBefore.Rows, "Cash");
        Assert.Equal(Count(cashBefore, 1) + 1, Count(cash, 1));
        Assert.Equal(Money(cashBefore, 2) + 44.80m, Money(cash, 2));

        // A method that only ever gave money back still appears, with a negative net: the terminal the
        // cashier has to reconcile has to see it.
        var upi = Row(outcome.MethodsAfter.Rows, "UPI");
        Assert.Equal(0, Count(upi, 1));
        Assert.Equal(0m, Money(upi, 2));
        Assert.Equal(22.40m, Money(upi, 3));
        Assert.Equal(-22.40m, Money(upi, 4));
    }

    /// <summary>
    /// A refund belongs to the business date it was given, not to the day it was keyed in or to the day of
    /// the sale. A return dated tomorrow must be invisible in today's takings and counted in full tomorrow,
    /// where it is a day that took no money at all.
    /// </summary>
    [Fact]
    public async Task A_return_is_reported_on_the_business_date_it_was_given()
    {
        var batch = $"B-{Unique()}";
        var tomorrow = Today.AddDays(1);

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var reports = services.GetRequiredService<IReportQueries>();

            var dailyToday = new ReportRequest { Kind = ReportKind.DailySales, From = Today, To = Today };
            var before = await reports.RunAsync(dailyToday);

            var medicineId = await SeedMedicineAsync(services, $"Date boundary test {Unique()}", taxRatePercent: 12m);
            var batchId = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 4m, 4.00m, 20.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();
            var returns = services.GetRequiredService<ISaleReturnCatalog>();

            var checkout = await sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 2m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 44.80m }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            var dated = await returns.RecordAsync(new SalesReturnDraft
            {
                SaleId = checkout.Value!.Id,
                ReturnedBy = Operator,
                Reason = "Taken back the next day",
                BusinessDate = tomorrow,
                Lines =
                [
                    new SalesReturnLineDraft
                    {
                        LineNumber = 1,
                        MedicineBatchId = batchId,
                        Quantity = 1m,
                        Disposition = ReturnDisposition.Quarantine,
                    },
                ],
            });

            Assert.True(dated.Succeeded, dated.Message);

            return new
            {
                Before = before,
                DailyAfter = await reports.RunAsync(dailyToday),
                Tomorrow = await reports.RunAsync(
                    new ReportRequest { Kind = ReportKind.DailySales, From = tomorrow, To = tomorrow }),
                ReturnsToday = await reports.RunAsync(
                    new ReportRequest { Kind = ReportKind.SalesReturns, From = Today, To = Today }),
                ReturnsTomorrow = await reports.RunAsync(
                    new ReportRequest { Kind = ReportKind.SalesReturns, From = tomorrow, To = tomorrow }),
                Dated = dated.Value!,
            };
        });

        // Today's takings know nothing about tomorrow's refund.
        var before = outcome.Before.Rows.SingleOrDefault(row => row[DailyDate] is DateOnly date && date == Today);
        var today = Assert.Single(
            outcome.DailyAfter.Rows,
            row => row[DailyDate] is DateOnly date && date == Today);

        Assert.Equal(Money(before, DailyRefunds), Money(today, DailyRefunds));
        Assert.Equal(Count(before, DailyReturns), Count(today, DailyReturns));

        // Tomorrow is a day that only paid money back: no invoices, no sales, and a negative margin.
        var tomorrowRow = Assert.Single(
            outcome.Tomorrow.Rows,
            row => row[DailyDate] is DateOnly date && date == tomorrow);
        Assert.Equal(0, Count(tomorrowRow, DailyInvoices));
        Assert.Equal(0m, Money(tomorrowRow, DailySales));
        Assert.Equal(0m, Money(tomorrowRow, DailyUnitsSold));
        Assert.Equal(1m, Money(tomorrowRow, DailyUnitsReturned));
        Assert.Equal(22.40m, Money(tomorrowRow, DailyRefunds));
        Assert.Equal(-22.40m, Money(tomorrowRow, DailyNet));
        Assert.Equal(-16.00m, Money(tomorrowRow, DailyMargin));

        // The returns report agrees about which day the return belongs to.
        Assert.DoesNotContain(
            outcome.ReturnsToday.Rows,
            row => (string?)row[1] == outcome.Dated.ReturnNumber);

        var line = Assert.Single(
            outcome.ReturnsTomorrow.Rows,
            row => (string?)row[1] == outcome.Dated.ReturnNumber);

        Assert.Equal(tomorrow, line[0]);
        Assert.Equal(1m, Money(line, 5));
        Assert.Equal(22.40m, Money(line, 6));
        Assert.Equal(ReturnDisposition.Quarantine, line[7]);
        Assert.Equal(Operator, line[8]);
        Assert.Equal("Taken back the next day", line[9]);
    }

    /// <summary>
    /// The valuation is only useful if it adds up: what the shelf cost, what it would sell for, and the
    /// difference, for exactly the batches that still hold stock.
    /// </summary>
    [Fact]
    public async Task The_valuation_report_agrees_with_the_batches_it_summarises()
    {
        var early = $"B-{Unique()}";
        var late = $"B-{Unique()}";
        var medicineName = $"Valuation test {Unique()}";

        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var reports = services.GetRequiredService<IReportQueries>();
            var medicineId = await SeedMedicineAsync(services, medicineName);

            await ReceiveAsync(services, medicineId, (early, Today.AddDays(30), 10m, 4.00m, 20.00m));
            await ReceiveAsync(services, medicineId, (late, Today.AddYears(1), 5m, 3.00m, 18.00m));

            var sales = services.GetRequiredService<ISaleCatalog>();

            // Two strips out of the batch expiring soonest, so the two batches hold different amounts.
            var checkout = await sales.CheckoutAsync(new SaleDraft
            {
                SoldBy = Operator,
                Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 2m }],
                Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 40.00m }],
            });

            Assert.True(checkout.Succeeded, checkout.Message);

            return new
            {
                Report = await reports.RunAsync(
                    new ReportRequest { Kind = ReportKind.InventoryValuation, From = Today, To = Today }),
                Early = await BatchAsync(services, early),
                Late = await BatchAsync(services, late),
            };
        });

        var row = Assert.Single(outcome.Report.Rows, candidate => (string?)candidate[0] == medicineName);

        // Ten arrived at 4.00 and five at 3.00; two left the shelf from the batch expiring first.
        Assert.Equal(2, Count(row, 1));
        Assert.Equal(13m, Money(row, 2));
        Assert.Equal(47.00m, Money(row, 3));
        Assert.Equal(250.00m, Money(row, 4));
        Assert.Equal(203.00m, Money(row, 5));

        // And the shelf itself holds the same amount the report valued.
        Assert.Equal(8m, outcome.Early.QuantityOnHand);
        Assert.Equal(5m, outcome.Late.QuantityOnHand);
        Assert.Equal(Money(row, 2), outcome.Early.QuantityOnHand + outcome.Late.QuantityOnHand);

        var totals = outcome.Report.Rows[^1];
        Assert.Equal("Total", totals[0]);

        var rows = outcome.Report.Rows.Take(outcome.Report.Rows.Count - 1).ToList();
        Assert.Equal(rows.Sum(candidate => Money(candidate, 2)), Money(totals, 2));
        Assert.Equal(rows.Sum(candidate => Money(candidate, 3)), Money(totals, 3));
        Assert.Equal(rows.Sum(candidate => Money(candidate, 4)), Money(totals, 4));
        Assert.Equal(rows.Sum(candidate => Money(candidate, 5)), Money(totals, 5));
    }

    /// <summary>
    /// A report leaves the application as a file an operator can open. The file has to carry the report's
    /// parameters with it, quote what a spreadsheet would otherwise split, and keep text an operator typed
    /// from being executed as a formula.
    /// </summary>
    [Fact]
    public async Task A_report_exports_to_csv_with_its_parameters_and_a_guard_against_formulas()
    {
        var path = Path.Combine(Path.GetTempPath(), "MediStock-Test-Reports", $"{Unique()}.csv");
        var batch = $"B-{Unique()}";

        try
        {
            var outcome = await _fixture.InScopeAsync(async services =>
            {
                var reports = services.GetRequiredService<IReportQueries>();
                var exporter = services.GetRequiredService<ICsvExporter>();

                var medicineId = await SeedMedicineAsync(services, $"Export test {Unique()}");
                var batchId = await ReceiveAsync(services, medicineId, (batch, Today.AddYears(1), 4m, 4.00m, 20.00m));

                var sales = services.GetRequiredService<ISaleCatalog>();
                var returns = services.GetRequiredService<ISaleReturnCatalog>();

                var checkout = await sales.CheckoutAsync(new SaleDraft
                {
                    SoldBy = Operator,
                    Lines = [new SaleLineDraft { MedicineId = medicineId, Quantity = 1m }],
                    Payments = [new PaymentDraft { Method = PaymentMethod.Cash, Amount = 20.00m }],
                });

                Assert.True(checkout.Succeeded, checkout.Message);

                var recorded = await returns.RecordAsync(new SalesReturnDraft
                {
                    SaleId = checkout.Value!.Id,
                    ReturnedBy = Operator,
                    Reason = "Seal broken, strip kept",
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

                Assert.True(recorded.Succeeded, recorded.Message);

                var report = await reports.RunAsync(
                    new ReportRequest { Kind = ReportKind.SalesReturns, From = Today, To = Today });

                var written = await exporter.ExportAsync(report, path);
                var document = await File.ReadAllTextAsync(written);

                // Re-exporting replaces the file rather than appending a second copy of the same figures.
                await exporter.ExportAsync(report, written);

                return (
                    Report: report,
                    Recorded: recorded.Value!,
                    Path: written,
                    Document: document,
                    Replaced: await File.ReadAllTextAsync(written));
            });

            Assert.True(File.Exists(outcome.Path));
            Assert.Equal(Path.GetFullPath(path), outcome.Path);

            var document = outcome.Document;
            var lines = document.Split("\r\n");

            Assert.EndsWith("\r\n", document, StringComparison.Ordinal);
            Assert.Equal(outcome.Report.Title, lines[0]);
            Assert.Equal(outcome.Report.Subtitle, lines[1]);
            Assert.StartsWith("Generated ", lines[2], StringComparison.Ordinal);
            Assert.Equal(string.Empty, lines[3]);
            Assert.Equal(
                string.Join(',', outcome.Report.Columns.Select(column => column.Header)),
                lines[4]);

            // Title, subtitle, generated, blank, header, one line per row, and nothing left over.
            Assert.Equal(outcome.Report.Rows.Count + 6, lines.Length);

            Assert.Contains(
                lines,
                line => line.Contains(outcome.Recorded.ReturnNumber, StringComparison.Ordinal)
                    && line.Contains("Destroyed", StringComparison.Ordinal)
                    && line.Contains("20.00", StringComparison.Ordinal));

            Assert.Equal(lines.Length, outcome.Replaced.Split("\r\n").Length);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Text an operator typed is kept out of a spreadsheet's formula engine, while amounts stay numbers.
    /// </summary>
    [Fact]
    public async Task Exported_text_that_looks_like_a_formula_is_neutralised()
    {
        var path = Path.Combine(Path.GetTempPath(), "MediStock-Test-Reports", $"{Unique()}.csv");

        try
        {
            var report = new ReportTable(
                "Formula guard",
                "Test rows",
                [new ReportColumn("Reason"), new ReportColumn("Amount", IsNumeric: true)],
                [
                    ["=SUM(A1:A2)", -22.40m],
                    ["+44", 0.00m],
                    ["@cmd", 1.50m],
                    ["Seal broken, strip kept", 3.00m],
                ]);

            using var scope = _fixture.Services.CreateScope();
            var exporter = scope.ServiceProvider.GetRequiredService<ICsvExporter>();
            var written = await exporter.ExportAsync(report, path);
            var lines = (await File.ReadAllTextAsync(written)).Split("\r\n");

            Assert.Equal("'=SUM(A1:A2),-22.40", lines[5]);
            Assert.Equal("'+44,0.00", lines[6]);
            Assert.Equal("'@cmd,1.50", lines[7]);

            // A value holding the separator is quoted, and an amount is never dressed up as text.
            Assert.Equal("\"Seal broken, strip kept\",3.00", lines[8]);

            await exporter.ExportAsync(report, written);
            Assert.Equal(10, (await File.ReadAllTextAsync(written)).Split("\r\n").Length);
        }
        finally
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    /// <summary>
    /// Every report the screen offers can actually be run, and comes back as a table the viewer can draw:
    /// a title, a subtitle stating what was asked for, non-empty and unique headers, and rectangular rows
    /// whose values all fit their column. A definition with no query behind it, or a query whose rows are
    /// the wrong width, would otherwise only show up as an empty grid in front of an operator.
    /// </summary>
    [Fact]
    public async Task Every_report_the_screen_offers_can_be_run_and_comes_back_as_a_table()
    {
        var outcome = await _fixture.InScopeAsync(async services =>
        {
            var reports = services.GetRequiredService<IReportQueries>();
            var tables = new List<(ReportDefinition Definition, ReportTable Table)>();

            foreach (var definition in ReportDefinitions.All)
            {
                var table = await reports.RunAsync(new ReportRequest
                {
                    Kind = definition.Kind,
                    From = Today.AddDays(-90),
                    To = Today,
                    HorizonDays = ReportRequest.DefaultHorizonDays,
                    Take = 50,
                });

                tables.Add((definition, table));
            }

            return tables;
        });

        // A definition added twice would be a second, invisible copy of the same report.
        Assert.Equal(
            ReportDefinitions.All.Count,
            ReportDefinitions.All.Select(definition => definition.Kind).Distinct().Count());

        Assert.Equal(ReportDefinitions.All.Count, outcome.Count);

        foreach (var (definition, table) in outcome)
        {
            Assert.Equal(definition.Title, table.Title);
            Assert.False(string.IsNullOrWhiteSpace(table.Subtitle), $"{definition.Title} has no subtitle.");

            Assert.NotEmpty(table.Columns);
            Assert.All(table.Columns, column =>
                Assert.False(string.IsNullOrWhiteSpace(column.Header), $"{definition.Title} has a nameless column."));

            Assert.Equal(
                table.Columns.Count,
                table.Columns.Select(column => column.Header).Distinct(StringComparer.OrdinalIgnoreCase).Count());

            // No report may hand back a row the grid cannot draw: every row is exactly as wide as the
            // columns say it is.
            Assert.All(table.Rows, row =>
                Assert.Equal(table.Columns.Count, row.Count));

            // A column declared numeric is drawn right-aligned and written to CSV unquoted, so it must
            // hold a number in every row but the one summary row a report is allowed to end with. That
            // row carries a label and the figures worth adding up; the rest of it is deliberately blank.
            var summary = table.Rows.Count > 0 && table.Rows[^1].Any(cell => cell is null)
                ? table.Rows[^1]
                : null;

            foreach (var (row, number) in table.Rows.Select((row, number) => (row, number)))
            {
                if (ReferenceEquals(row, summary))
                {
                    Assert.False(
                        string.IsNullOrWhiteSpace(row[0] as string),
                        $"{definition.Title} ends with a summary row that does not say what it is.");

                    Assert.Contains(row.Skip(1), cell => cell is decimal or int or long);
                    continue;
                }

                for (var column = 0; column < table.Columns.Count; column++)
                {
                    var expected = table.Columns[column];
                    var value = row[column];

                    if (!expected.IsNumeric)
                    {
                        continue;
                    }

                    Assert.True(
                        value is decimal or int or long,
                        $"{definition.Title} row {number} has no figure for {expected.Header}.");
                }
            }

            // The reports the screen only shows on request must honour it.
            if (definition.UsesTake)
            {
                Assert.True(table.Rows.Count <= 50, $"{definition.Title} ignored the row limit.");
            }

            if (!definition.UsesDateRange && !definition.UsesHorizonDays)
            {
                // A snapshot report describes the stock as it stands, not a range of dates.
                Assert.StartsWith("Stock as of ", table.Subtitle, StringComparison.Ordinal);
            }
        }
    }

    private static IReadOnlyList<object?>? Row(IReadOnlyList<IReadOnlyList<object?>> rows, string method)
        => rows.SingleOrDefault(row => row[0] is PaymentMethod candidate && ReportText.Describe(candidate) == method);

    private static decimal Money(IReadOnlyList<object?>? row, int index)
        => row is null ? 0m : Convert.ToDecimal(row[index], CultureInfo.InvariantCulture);

    private static int Count(IReadOnlyList<object?>? row, int index)
        => row is null ? 0 : Convert.ToInt32(row[index], CultureInfo.InvariantCulture);

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

    private async Task<int> ReceiveAsync(
        IServiceProvider services,
        int medicineId,
        (string BatchNumber, DateOnly ExpiryDate, decimal Quantity, decimal UnitCost, decimal SellingPrice) line)
    {
        var supplier = await services.GetRequiredService<ISupplierCatalog>().CreateAsync(
            new SupplierDraft { Name = $"Report test supplier {Unique()}" });
        Assert.True(supplier.Succeeded, supplier.Message);

        var catalog = services.GetRequiredService<IPurchaseCatalog>();

        var created = await catalog.CreateAsync(new PurchaseDraft
        {
            SupplierId = supplier.Value!.Id,
            InvoiceNumber = $"RPT-{Unique()}",
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

    private static async Task<BatchStockDto> BatchAsync(IServiceProvider services, string batchNumber)
    {
        var batches = await services.GetRequiredService<IStockQueries>().ListBatchesAsync(
            new BatchStockQuery { Term = batchNumber, InStockOnly = false });

        return Assert.Single(batches);
    }

    private static string Unique() => Guid.NewGuid().ToString("N")[..8];
}
