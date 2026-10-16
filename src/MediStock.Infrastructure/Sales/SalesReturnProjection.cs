using MediStock.Application.Sales;
using MediStock.Domain.Sales;

namespace MediStock.Infrastructure.Sales;

/// <summary>
/// Shared projections from the return aggregate to the shapes the screens and the reports use.
/// </summary>
/// <remarks>
/// Kept in one place because an invoice's list of returns and the Returns screen must not describe the
/// same document differently. The navigation properties are read defensively: a return is always loaded
/// with its invoice and lines by <c>EfSaleReturnCatalog</c>, but the summary form is also projected from
/// an invoice that has only loaded its returns.
/// </remarks>
internal static class SalesReturnProjection
{
    internal static SalesReturnSummaryDto Summarise(SalesReturn salesReturn)
    {
        ArgumentNullException.ThrowIfNull(salesReturn);

        return new SalesReturnSummaryDto(
            salesReturn.Id,
            salesReturn.ReturnNumber,
            salesReturn.BusinessDate,
            salesReturn.ReturnedBy,
            salesReturn.RefundTotal,
            salesReturn.RefundMethod,
            salesReturn.ReturnedQuantity,
            salesReturn.RestockedQuantity,
            salesReturn.Reason);
    }

    internal static SalesReturnDto Detail(SalesReturn salesReturn)
    {
        ArgumentNullException.ThrowIfNull(salesReturn);

        var sale = salesReturn.Sale;

        return new SalesReturnDto
        {
            Id = salesReturn.Id,
            ReturnNumber = salesReturn.ReturnNumber,
            BusinessDate = salesReturn.BusinessDate,
            ReturnedAtUtc = salesReturn.ReturnedAtUtc,
            ReturnedBy = salesReturn.ReturnedBy,
            SaleId = salesReturn.SaleId,
            InvoiceNumber = sale?.InvoiceNumber ?? string.Empty,
            InvoiceBusinessDate = sale?.BusinessDate ?? salesReturn.BusinessDate,
            CustomerName = sale?.CustomerName,
            Reason = salesReturn.Reason,
            Notes = salesReturn.Notes,
            RefundTotal = salesReturn.RefundTotal,
            RefundMethod = salesReturn.RefundMethod,
            CreatedAtUtc = salesReturn.CreatedAtUtc,
            Lines =
            [
                .. salesReturn.Lines
                    .OrderBy(line => line.LineNumber)
                    .Select(line => new SalesReturnLineDto
                    {
                        LineNumber = line.LineNumber,
                        SaleLineId = line.SaleLineId,
                        InvoiceLineNumber = line.SaleLine?.LineNumber ?? 0,
                        MedicineBatchId = line.MedicineBatchId,
                        BatchNumber = line.MedicineBatch?.BatchNumber ?? string.Empty,
                        MedicineName = line.SaleLine?.MedicineName ?? string.Empty,
                        Quantity = line.Quantity,
                        RefundAmount = line.RefundAmount,
                        Disposition = line.Disposition,
                        Notes = line.Notes,
                    }),
            ],
        };
    }
}
