namespace MediStock.Application.Sales;

/// <summary>Where an exported receipt ended up, so the UI can offer to open or reveal it.</summary>
public sealed record ReceiptExport(string InvoiceNumber, string FilePath);

/// <summary>
/// Writes a printable document for a completed sale. Separated from the catalog because producing a
/// document is not a database operation, and because a receipt must be reprintable from a stored sale
/// without touching stock.
/// </summary>
public interface IReceiptExporter
{
    Task<ReceiptExport> ExportAsync(SaleDto sale, CancellationToken cancellationToken = default);
}
