using MediStock.Domain.Sales;

namespace MediStock.Application.Sales;

/// <summary>One returned quantity on a return document, ready to display or print.</summary>
public sealed record SalesReturnLineDto
{
    public int LineNumber { get; init; }

    public int SaleLineId { get; init; }

    /// <summary>Line number on the invoice being returned against.</summary>
    public int InvoiceLineNumber { get; init; }

    public int MedicineBatchId { get; init; }

    public string BatchNumber { get; init; } = string.Empty;

    public string MedicineName { get; init; } = string.Empty;

    public decimal Quantity { get; init; }

    public decimal RefundAmount { get; init; }

    public ReturnDisposition Disposition { get; init; }

    /// <summary>True when this line added stock back to its batch.</summary>
    public bool WasRestocked => Disposition == ReturnDisposition.Restock;

    public string? Notes { get; init; }
}

/// <summary>A recorded customer return.</summary>
public sealed record SalesReturnDto
{
    public int Id { get; init; }

    public string ReturnNumber { get; init; } = string.Empty;

    public DateOnly BusinessDate { get; init; }

    public DateTimeOffset ReturnedAtUtc { get; init; }

    public string ReturnedBy { get; init; } = string.Empty;

    public int SaleId { get; init; }

    /// <summary>Invoice the medicine came from.</summary>
    public string InvoiceNumber { get; init; } = string.Empty;

    public DateOnly InvoiceBusinessDate { get; init; }

    public string? CustomerName { get; init; }

    public string Reason { get; init; } = string.Empty;

    public string? Notes { get; init; }

    public decimal RefundTotal { get; init; }

    /// <summary>How the money was handed back.</summary>
    public PaymentMethod RefundMethod { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public IReadOnlyList<SalesReturnLineDto> Lines { get; init; } = [];

    public decimal ReturnedQuantity => Lines.Sum(line => line.Quantity);

    /// <summary>How much of the return went back onto the shelf.</summary>
    public decimal RestockedQuantity =>
        Lines.Where(line => line.Disposition == ReturnDisposition.Restock).Sum(line => line.Quantity);
}

/// <summary>A compact view of one return, for the list shown against an invoice.</summary>
public sealed record SalesReturnSummaryDto(
    int Id,
    string ReturnNumber,
    DateOnly BusinessDate,
    string ReturnedBy,
    decimal RefundTotal,
    PaymentMethod RefundMethod,
    decimal ReturnedQuantity,
    decimal RestockedQuantity,
    string Reason);

/// <summary>Filter for the returns history.</summary>
public sealed record SalesReturnQuery
{
    public const int MaxResults = 1000;

    /// <summary>Free text matched against the return number, invoice number and medicine name.</summary>
    public string? Term { get; init; }

    public DateOnly? FromBusinessDate { get; init; }

    public DateOnly? ToBusinessDate { get; init; }

    public int? SaleId { get; init; }

    public ReturnDisposition? Disposition { get; init; }

    public int Take { get; init; } = 200;
}
