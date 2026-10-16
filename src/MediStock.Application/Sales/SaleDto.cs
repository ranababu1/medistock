using MediStock.Domain.Sales;

namespace MediStock.Application.Sales;

/// <summary>One batch a line is expected to consume, with the batch details a receipt needs to print.</summary>
public sealed record SaleAllocationDto
{
    public int MedicineBatchId { get; init; }

    public string BatchNumber { get; init; } = string.Empty;

    public DateOnly ExpiryDate { get; init; }

    public decimal Quantity { get; init; }

    /// <summary>How much of <see cref="Quantity"/> has been returned from this batch.</summary>
    public decimal ReturnedQuantity { get; init; }

    /// <summary>How much of this batch a customer could still bring back.</summary>
    public decimal ReturnableQuantity => Quantity - ReturnedQuantity;
}

/// <summary>A priced invoice line, with the batches it will consume.</summary>
public sealed record SaleLineDto
{
    public int LineNumber { get; init; }

    public int MedicineId { get; init; }

    public string MedicineName { get; init; } = string.Empty;

    public decimal Quantity { get; init; }

    public decimal UnitPrice { get; init; }

    public decimal UnitCost { get; init; }

    public decimal GrossAmount { get; init; }

    public decimal DiscountAmount { get; init; }

    public decimal TaxableAmount { get; init; }

    public decimal TaxRatePercent { get; init; }

    public decimal TaxAmount { get; init; }

    public decimal LineTotal { get; init; }

    public decimal LineCost { get; init; }

    /// <summary>How much of <see cref="Quantity"/> has been returned by customers.</summary>
    public decimal ReturnedQuantity { get; init; }

    /// <summary>Money refunded against this line, including the tax charged on it.</summary>
    public decimal ReturnedAmount { get; init; }

    /// <summary>How much of this line a customer could still bring back.</summary>
    public decimal ReturnableQuantity => Quantity - ReturnedQuantity;

    /// <summary>True once the whole line has been returned.</summary>
    public bool IsFullyReturned => ReturnedQuantity == Quantity;

    public IReadOnlyList<SaleAllocationDto> Allocations { get; init; } = [];
}

/// <summary>A settlement recorded against an invoice.</summary>
public sealed record PaymentDto(
    PaymentMethod Method,
    decimal Amount,
    string? Reference,
    DateTimeOffset PaidAtUtc);

/// <summary>A completed sale, ready to display or print.</summary>
public sealed record SaleDto
{
    public int Id { get; init; }

    public string InvoiceNumber { get; init; } = string.Empty;

    public DateOnly BusinessDate { get; init; }

    public DateTimeOffset SoldAtUtc { get; init; }

    public string SoldBy { get; init; } = string.Empty;

    public string? CustomerName { get; init; }

    public string? CustomerPhone { get; init; }

    public string? Notes { get; init; }

    public SaleStatus Status { get; init; } = SaleStatus.Completed;

    public decimal SubTotal { get; init; }

    public decimal DiscountTotal { get; init; }

    public decimal TaxableTotal { get; init; }

    public decimal TaxTotal { get; init; }

    public decimal Total { get; init; }

    public decimal CostTotal { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset? CancelledAtUtc { get; init; }

    public string? CancelledBy { get; init; }

    public string? CancellationReason { get; init; }

    /// <summary>Money refunded across all returns against this invoice.</summary>
    public decimal ReturnedTotal { get; init; }

    public IReadOnlyList<SalesReturnSummaryDto> Returns { get; init; } = [];

    public IReadOnlyList<SaleLineDto> Lines { get; init; } = [];

    public IReadOnlyList<PaymentDto> Payments { get; init; } = [];

    public decimal Margin => Total - TaxTotal - CostTotal;

    public decimal UnitsReturned => Lines.Sum(line => line.ReturnedQuantity);

    /// <summary>True once every line has been returned in full.</summary>
    public bool IsFullyReturned => Lines.Count > 0 && Lines.All(line => line.IsFullyReturned);

    /// <summary>True when the invoice may be cancelled: nothing left out with the customer, and not already cancelled.</summary>
    public bool CanCancel => Status == SaleStatus.Completed && IsFullyReturned;
}

/// <summary>
/// What a basket would cost and which batches it would consume, without changing anything. The
/// point-of-sale screen shows this so the operator can see the allocation before taking money.
/// </summary>
public sealed record SaleQuote
{
    /// <summary>True when the basket could be checked out as it stands.</summary>
    public bool IsCheckoutReady { get; init; }

    /// <summary>Plain-language reasons the basket cannot be checked out yet.</summary>
    public IReadOnlyList<string> Blockers { get; init; } = [];

    public string InvoicePreview { get; init; } = string.Empty;

    public IReadOnlyList<SaleLineDto> Lines { get; init; } = [];

    public decimal SubTotal { get; init; }

    public decimal DiscountTotal { get; init; }

    public decimal TaxableTotal { get; init; }

    public decimal TaxTotal { get; init; }

    public decimal Total { get; init; }

    public decimal CostTotal { get; init; }

    public decimal Margin => Total - TaxTotal - CostTotal;
}

/// <summary>Filter for the sales history screen.</summary>
public sealed record SaleQuery
{
    public const int MaxResults = 1000;

    /// <summary>Free text matched against the invoice number, customer name and medicine name.</summary>
    public string? Term { get; init; }

    public DateOnly? FromBusinessDate { get; init; }

    public DateOnly? ToBusinessDate { get; init; }

    public SaleStatus? Status { get; init; }

    public PaymentMethod? PaymentMethod { get; init; }

    public int Take { get; init; } = 200;
}
