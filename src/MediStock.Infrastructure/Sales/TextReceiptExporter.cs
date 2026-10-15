using System.Globalization;
using System.Text;
using MediStock.Application.Configuration;
using MediStock.Application.Sales;
using MediStock.Domain.Sales;
using Microsoft.Extensions.Logging;

namespace MediStock.Infrastructure.Sales;

/// <summary>
/// Writes a plain-text invoice to <see cref="MediStockOptions.ReceiptFolder"/>.
/// </summary>
/// <remarks>
/// <para>
/// Text rather than a formatted document because the first release only has to produce something a
/// cashier can hand over, keep for the file, and reprint. A layout that a thermal printer accepts is a
/// later concern; the numbers on this document are the invoice's own stored figures, not a
/// re-calculation, so a receipt can never contradict its invoice.
/// </para>
/// <para>
/// Exporting is idempotent: reprinting overwrites the file with a fresh copy and stamps it as a
/// duplicate, so nothing about a reprint can alter the recorded sale. No stock or money is touched.
/// </para>
/// </remarks>
internal sealed class TextReceiptExporter : IReceiptExporter
{
    private const int Width = 44;

    private readonly MediStockOptions _options;
    private readonly ILogger<TextReceiptExporter> _logger;

    public TextReceiptExporter(MediStockOptions options, ILogger<TextReceiptExporter> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<ReceiptExport> ExportAsync(SaleDto sale, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sale);

        var folder = ResolveFolder(_options.ReceiptFolder);
        Directory.CreateDirectory(folder);

        var path = Path.Combine(folder, $"{FileName(sale)}.txt");

        await File.WriteAllTextAsync(path, Render(sale), new UTF8Encoding(encoderShouldEmitUTF8Identifier: false), cancellationToken)
            .ConfigureAwait(false);

        _logger.LogInformation(
            "Wrote the receipt for invoice {InvoiceNumber} to {Path}.",
            sale.InvoiceNumber,
            path);

        return new ReceiptExport(sale.InvoiceNumber, path);
    }

    /// <summary>A relative folder is resolved against the user's documents so the executable's own working directory does not matter.</summary>
    private static string ResolveFolder(string configured)
    {
        var folder = string.IsNullOrWhiteSpace(configured) ? @"MediStock\Receipts" : configured.Trim();

        return Path.GetFullPath(
            Path.IsPathRooted(folder)
                ? folder
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), folder));
    }

    /// <summary>
    /// The invoice number is already restricted to digits, letters and dashes, but it becomes a file name
    /// here, so anything a file system could object to is replaced rather than trusted.
    /// </summary>
    private static string FileName(SaleDto sale)
    {
        var safe = new string([.. sale.InvoiceNumber
            .Select(character => Path.GetInvalidFileNameChars().Contains(character) || character is '\\' or '/' or ':'
                ? '_'
                : character)]);

        return string.IsNullOrWhiteSpace(safe) ? $"sale-{sale.Id}" : safe;
    }

    private static string Render(SaleDto sale)
    {
        var text = new StringBuilder();
        var culture = CultureInfo.InvariantCulture;

        Line(text, "TAX INVOICE");
        Line(text, $"{sale.InvoiceNumber}  ({Local(sale.SoldAtUtc)})");
        Line(text, $"Business date {sale.BusinessDate:yyyy-MM-dd}");
        Line(text);
        Line(text, $"Served by {sale.SoldBy}");

        if (!string.IsNullOrWhiteSpace(sale.CustomerName))
        {
            Line(text, $"Customer   {sale.CustomerName}");
        }

        if (!string.IsNullOrWhiteSpace(sale.CustomerPhone))
        {
            Line(text, $"Phone      {sale.CustomerPhone}");
        }

        Rule(text);
        Line(text, Row("Item", "Qty", "Rate", "Amount"));
        Rule(text);

        foreach (var line in sale.Lines)
        {
            // A long name is wrapped rather than clipped: an abbreviation on an invoice is a guessing game.
            foreach (var wrapped in Wrap($"{line.LineNumber}. {line.MedicineName}", Width))
            {
                text.Append(wrapped);
                text.Append('\n');
            }

            Line(text, Row(
                $"  @{line.TaxRatePercent.ToString("0.##", culture)}% tax",
                Quantity(line.Quantity),
                Money(line.UnitPrice),
                Money(line.GrossAmount)));

            if (line.DiscountAmount > 0m)
            {
                Line(text, Row("  less discount", string.Empty, string.Empty, $"-{Money(line.DiscountAmount)}"));
            }

            // Printed because the operator must be able to trace which physical stock left the shelf.
            foreach (var allocation in line.Allocations)
            {
                Line(text, $"  batch {allocation.BatchNumber} exp {allocation.ExpiryDate:yyyy-MM-dd} x{Quantity(allocation.Quantity)}");
            }
        }

        Rule(text);
        Line(text, Row("Subtotal", string.Empty, string.Empty, Money(sale.SubTotal)));

        if (sale.DiscountTotal > 0m)
        {
            Line(text, Row("Discount", string.Empty, string.Empty, $"-{Money(sale.DiscountTotal)}"));
        }

        Line(text, Row("Taxable", string.Empty, string.Empty, Money(sale.TaxableTotal)));
        Line(text, Row("Tax", string.Empty, string.Empty, Money(sale.TaxTotal)));
        Line(text, Row("TOTAL", string.Empty, string.Empty, Money(sale.Total)));
        Rule(text);

        foreach (var payment in sale.Payments)
        {
            Line(text, Row(payment.Method.ToString(), string.Empty, string.Empty, Money(payment.Amount)));
        }

        if (sale.Status != SaleStatus.Completed)
        {
            Line(text);
            Line(text, $"*** THIS INVOICE IS {sale.Status.ToString().ToUpperInvariant()} ***");
        }

        if (!string.IsNullOrWhiteSpace(sale.Notes))
        {
            Line(text);
            Line(text, "Notes:");
            foreach (var wrapped in Wrap(sale.Notes, Width))
            {
                text.Append(wrapped);
                text.Append('\n');
            }
        }

        Rule(text);
        Line(text, "Reprinted copy — the invoice above is the record of sale.");

        return text.ToString();

        static void Line(StringBuilder target, string value = "")
        {
            target.Append(value);
            target.Append('\n');
        }

        static void Rule(StringBuilder target) => Line(target, new string('-', Width));
    }

    private static string Money(decimal value) => value.ToString("0.00", CultureInfo.InvariantCulture);

    /// <summary>Drops trailing zeros so a whole unit reads as 1 rather than 1.000 on the docket.</summary>
    private static string Quantity(decimal value) => value.ToString("0.###", CultureInfo.InvariantCulture);

    private static string Row(string left, string quantity, string rate, string amount)
    {
        const int quantityWidth = 7;
        const int rateWidth = 9;
        const int amountWidth = 10;

        var head = left.Length > Width - quantityWidth - rateWidth - amountWidth
            ? left[..(Width - quantityWidth - rateWidth - amountWidth)]
            : left.PadRight(Width - quantityWidth - rateWidth - amountWidth);

        return head
            + quantity.PadLeft(quantityWidth)
            + rate.PadLeft(rateWidth)
            + amount.PadLeft(amountWidth);
    }

    private static IEnumerable<string> Wrap(string value, int width)
    {
        var words = value.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var line = new StringBuilder();

        foreach (var word in words)
        {
            if (line.Length > 0 && line.Length + 1 + word.Length > width)
            {
                yield return line.ToString();
                line.Clear();
            }

            if (line.Length > 0)
            {
                line.Append(' ');
            }

            line.Append(word);
        }

        if (line.Length > 0)
        {
            yield return line.ToString();
        }
    }

    private static string Local(DateTimeOffset utc) => utc.ToLocalTime().ToString("yyyy-MM-dd HH:mm", CultureInfo.InvariantCulture);
}
