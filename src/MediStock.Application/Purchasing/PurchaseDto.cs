using MediStock.Domain.Purchasing;

namespace MediStock.Application.Purchasing;

/// <summary>One line of a purchase, including the batch it created once received.</summary>
public sealed record PurchaseLineDto
{
    public int Id { get; init; }

    public int MedicineId { get; init; }

    public string MedicineName { get; init; } = string.Empty;

    public string BatchNumber { get; init; } = string.Empty;

    public DateOnly ExpiryDate { get; init; }

    public decimal Quantity { get; init; }

    public decimal QuantityReceived { get; init; }

    public decimal UnitCost { get; init; }

    public decimal SellingPrice { get; init; }

    public decimal LineTotal { get; init; }

    /// <summary>The batch created for this line. Null until the purchase is received.</summary>
    public int? MedicineBatchId { get; init; }
}

/// <summary>A purchase header with its lines.</summary>
public sealed record PurchaseDto
{
    public int Id { get; init; }

    public int SupplierId { get; init; }

    public string SupplierName { get; init; } = string.Empty;

    public string InvoiceNumber { get; init; } = string.Empty;

    public DateOnly InvoiceDate { get; init; }

    public string? Notes { get; init; }

    public PurchaseStatus Status { get; init; } = PurchaseStatus.Draft;

    public decimal TotalValue { get; init; }

    public DateOnly? ReceivedOn { get; init; }

    public DateTimeOffset? ReceivedAtUtc { get; init; }

    public string? ReceivedBy { get; init; }

    public DateTimeOffset? ClosedAtUtc { get; init; }

    public string? ClosedBy { get; init; }

    public string? StatusReason { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public DateTimeOffset UpdatedAtUtc { get; init; }

    public IReadOnlyList<PurchaseLineDto> Lines { get; init; } = [];
}

/// <summary>Purchase header without its lines, for list screens.</summary>
public sealed record PurchaseSummaryDto
{
    public int Id { get; init; }

    public int SupplierId { get; init; }

    public string SupplierName { get; init; } = string.Empty;

    public string InvoiceNumber { get; init; } = string.Empty;

    public DateOnly InvoiceDate { get; init; }

    public PurchaseStatus Status { get; init; } = PurchaseStatus.Draft;

    public decimal TotalValue { get; init; }

    public int LineCount { get; init; }

    public DateOnly? ReceivedOn { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }
}
