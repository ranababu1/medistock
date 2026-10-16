namespace MediStock.Application.Suppliers;

/// <summary>One quantity of one batch as shown on a supplier return.</summary>
public sealed record SupplierReturnLineDto
{
    public int LineNumber { get; init; }

    public int MedicineBatchId { get; init; }

    public string BatchNumber { get; init; } = string.Empty;

    public string MedicineName { get; init; } = string.Empty;

    public decimal Quantity { get; init; }

    /// <summary>What the pharmacy paid for one unit, copied onto the line when the return was recorded.</summary>
    public decimal UnitCost { get; init; }

    /// <summary>The value of the quantity at cost: what the supplier is expected to credit.</summary>
    public decimal CostValue { get; init; }

    public string? Notes { get; init; }
}

/// <summary>A supplier return as the Returns screen and the reports show it.</summary>
public sealed record SupplierReturnDto
{
    public int Id { get; init; }

    public string ReturnNumber { get; init; } = string.Empty;

    public DateOnly BusinessDate { get; init; }

    public DateTimeOffset ReturnedAtUtc { get; init; }

    public string ReturnedBy { get; init; } = string.Empty;

    public int SupplierId { get; init; }

    public string SupplierName { get; init; } = string.Empty;

    public string Reason { get; init; } = string.Empty;

    public string? Notes { get; init; }

    public string? SupplierCreditNoteNumber { get; init; }

    public decimal TotalCostValue { get; init; }

    public DateTimeOffset CreatedAtUtc { get; init; }

    public IReadOnlyList<SupplierReturnLineDto> Lines { get; init; } = [];
}

/// <summary>One return in the supplier-returns list.</summary>
public sealed record SupplierReturnSummaryDto(
    int Id,
    string ReturnNumber,
    DateOnly BusinessDate,
    string ReturnedBy,
    int SupplierId,
    string SupplierName,
    decimal TotalCostValue,
    decimal ReturnedQuantity,
    string Reason);

/// <summary>Filter for the supplier-returns list.</summary>
public sealed record SupplierReturnQuery
{
    public const int MaxResults = 500;

    public DateOnly? FromBusinessDate { get; init; }

    public DateOnly? ToBusinessDate { get; init; }

    public int? SupplierId { get; init; }

    /// <summary>Free text matched against the return number, the supplier credit note and the reason.</summary>
    public string? Term { get; init; }

    public int Take { get; init; } = 200;
}