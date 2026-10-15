using MediStock.Domain.Common;
using MediStock.Domain.Purchasing;

namespace MediStock.Application.Purchasing;

/// <summary>One ordered medicine on a purchase being drafted or edited.</summary>
public sealed record PurchaseLineDraft
{
    public int MedicineId { get; init; }

    /// <summary>Display-only. Never posted back to the server.</summary>
    public string MedicineName { get; init; } = string.Empty;

    public string BatchNumber { get; init; } = string.Empty;

    public DateOnly ExpiryDate { get; init; }

    public decimal Quantity { get; init; }

    public decimal UnitCost { get; init; }

    public decimal SellingPrice { get; init; }

    public decimal LineTotal => Money.Round(Quantity * UnitCost);
}

/// <summary>
/// User-supplied values for creating or editing a purchase. Validation reuses the domain aggregate so
/// the lifecycle rules exist in exactly one place.
/// </summary>
public sealed record PurchaseDraft
{
    public static PurchaseDraft Empty { get; } = new();

    public int SupplierId { get; init; }

    public string InvoiceNumber { get; init; } = string.Empty;

    public DateOnly InvoiceDate { get; init; } = DateOnly.FromDateTime(DateTime.Today);

    public string? Notes { get; init; }

    public IReadOnlyList<PurchaseLineDraft> Lines { get; init; } = [];

    public decimal TotalValue => Lines.Sum(line => line.LineTotal);

    public static PurchaseDraft FromDto(PurchaseDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);

        return new PurchaseDraft
        {
            SupplierId = dto.SupplierId,
            InvoiceNumber = dto.InvoiceNumber,
            InvoiceDate = dto.InvoiceDate,
            Notes = dto.Notes,
            Lines =
            [
                .. dto.Lines.Select(line => new PurchaseLineDraft
                {
                    MedicineId = line.MedicineId,
                    MedicineName = line.MedicineName,
                    BatchNumber = line.BatchNumber,
                    ExpiryDate = line.ExpiryDate,
                    Quantity = line.Quantity,
                    UnitCost = line.UnitCost,
                    SellingPrice = line.SellingPrice,
                }),
            ],
        };
    }

    /// <summary>
    /// Replays the draft through the domain aggregate and returns every rule violation, so the editor
    /// can show them all at once.
    /// </summary>
    public IReadOnlyList<string> Validate()
    {
        try
        {
            var purchase = Purchase.Create(SupplierId, InvoiceNumber, InvoiceDate, Notes, DateTimeOffset.UnixEpoch);

            foreach (var line in Lines)
            {
                purchase.AddLine(
                    line.MedicineId,
                    line.BatchNumber,
                    line.ExpiryDate,
                    line.Quantity,
                    line.UnitCost,
                    line.SellingPrice,
                    DateTimeOffset.UnixEpoch);
            }

            return [];
        }
        catch (DomainValidationException ex)
        {
            return [ex.Message];
        }
    }

    /// <summary>
    /// Builds a validated, unsaved aggregate. Throws <see cref="DomainValidationException"/> when
    /// invalid. The instance is left as a draft; receiving is a separate step.
    /// </summary>
    public Purchase ToNewPurchase(DateTimeOffset nowUtc)
    {
        var purchase = Purchase.Create(SupplierId, InvoiceNumber, InvoiceDate, Notes, nowUtc);

        foreach (var line in Lines)
        {
            purchase.AddLine(
                line.MedicineId,
                line.BatchNumber,
                line.ExpiryDate,
                line.Quantity,
                line.UnitCost,
                line.SellingPrice,
                nowUtc);
        }

        return purchase;
    }
}
